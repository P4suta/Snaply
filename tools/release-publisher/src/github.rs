use crate::core::{self, Asset, Digest, Release};
use crate::engine::Port;
use serde::Deserialize;
use sha2::{Digest as _, Sha256};
use std::fs::{self, File};
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};

const MAX_JSON: u64 = 8 * 1024 * 1024;
const MAX_ASSET: u64 = 2 * 1024 * 1024 * 1024;
const MAX_TAG_DEPTH: usize = 16;
const GITHUB_UPLOADS_ORIGIN: &str = "https://uploads.github.com";

#[derive(Deserialize)]
struct Object {
    sha: String,
    #[serde(rename = "type")]
    kind: String,
}

#[derive(Deserialize)]
struct Reference {
    object: Object,
}

#[derive(Deserialize)]
struct RemoteRelease {
    id: u64,
    tag_name: String,
    draft: bool,
}

#[derive(Deserialize)]
struct RemoteAsset {
    id: u64,
    name: String,
    size: u64,
    state: String,
}

#[derive(Deserialize)]
struct Commit {
    sha: String,
}

#[derive(Deserialize)]
struct Comparison {
    merge_base_commit: Commit,
}

pub struct GitHub {
    repo: String,
    tag: String,
    assets: PathBuf,
}

impl GitHub {
    pub fn new(repo: &str, tag: &str, assets: &Path) -> Result<Self, String> {
        Ok(Self {
            repo: repo.to_owned(),
            tag: tag.to_owned(),
            assets: assets
                .canonicalize()
                .map_err(|e| format!("asset directory: {e}"))?,
        })
    }

    pub fn check_identity(repo: &str, tag: &str, sha: &str) -> Result<(), String> {
        core::validate_identity(repo, tag, sha)?;
        let reference: Reference = json(&format!("repos/{repo}/git/ref/tags/{tag}"))?;
        let mut object = reference.object;
        for depth in 0..=MAX_TAG_DEPTH {
            if object.kind == "commit" {
                if object.sha != sha {
                    return Err("remote tag does not identify the selected commit".to_owned());
                }
                let comparison: Comparison = json(&format!("repos/{repo}/compare/{sha}...main"))?;
                if comparison.merge_base_commit.sha != sha {
                    return Err("release commit is not an ancestor of main".to_owned());
                }
                let version = api(
                    &format!("repos/{repo}/contents/version.txt?ref={sha}"),
                    &["-H", "Accept: application/vnd.github.raw+json"],
                )?;
                let version = std::str::from_utf8(&version).map_err(|e| e.to_string())?;
                if format!("v{}", version.trim()) != tag {
                    return Err("version.txt does not match the release tag".to_owned());
                }
                return Ok(());
            }
            if depth == MAX_TAG_DEPTH {
                break;
            }
            if object.kind != "tag" || !valid_sha(&object.sha) {
                return Err("remote tag object is malformed".to_owned());
            }
            let reference: Reference = json(&format!("repos/{repo}/git/tags/{}", object.sha))?;
            object = reference.object;
        }
        Err("remote tag nesting exceeds the supported bound of sixteen".to_owned())
    }

    fn release(&self, id: u64, include_published: bool) -> Result<Release, String> {
        let release: RemoteRelease = json(&format!("repos/{}/releases/{id}", self.repo))?;
        if release.id != id || id == 0 {
            return Err("release readback returned another identity".to_owned());
        }
        let assets = if release.draft || include_published {
            self.remote_assets(id)?
        } else {
            Vec::new()
        };
        Ok(Release {
            id,
            tag: release.tag_name,
            draft: release.draft,
            assets,
        })
    }

    fn remote_assets(&self, id: u64) -> Result<Vec<Asset>, String> {
        let pages: Vec<Vec<RemoteAsset>> = paginated(&format!(
            "repos/{}/releases/{id}/assets?per_page=100",
            self.repo
        ))?;
        let allowed = core::expected_names(&self.tag);
        let mut assets = Vec::new();
        for remote in pages.into_iter().flatten() {
            if remote.id == 0
                || remote.state != "uploaded"
                || remote.size > MAX_ASSET
                || !allowed.contains(&remote.name)
            {
                return Err(
                    "remote asset has an unexpected identity, state, name, or size".to_owned(),
                );
            }
            let endpoint = format!("repos/{}/releases/assets/{}", self.repo, remote.id);
            let mut child = gh_api(&endpoint)
                .args(["--method", "GET"])
                .args(["-H", "Accept: application/octet-stream"])
                .stdout(Stdio::piped())
                .spawn()
                .map_err(|e| format!("download asset: {e}"))?;
            let result = child
                .stdout
                .take()
                .ok_or("missing asset response stream".to_owned())
                .and_then(|stream| fingerprint_reader(stream.take(remote.size + 1)));
            let digest = match result {
                Ok((size, digest)) if size == remote.size => digest,
                other => {
                    let failure = match other {
                        Ok((size, _)) => format!(
                            "remote asset {} has {size} bytes, expected {}",
                            remote.name, remote.size
                        ),
                        Err(error) => {
                            format!("remote asset {} could not be read: {error}", remote.name)
                        }
                    };
                    return Err(format!("{failure}; {}", stop_child(&mut child)));
                }
            };
            let status = child
                .wait()
                .map_err(|e| format!("download asset status: {e}"))?;
            if !status.success() {
                return Err(format!("asset download failed with status {status}"));
            }
            assets.push(Asset {
                name: remote.name,
                size: remote.size,
                digest,
            });
        }
        Ok(assets)
    }

    fn local_path(&self, name: &str) -> Result<PathBuf, String> {
        if !core::expected_names(&self.tag)
            .iter()
            .any(|expected| expected == name)
        {
            return Err("asset name is outside the package inventory".to_owned());
        }
        let path = self.assets.join(name);
        let metadata = fs::symlink_metadata(&path).map_err(|e| format!("asset metadata: {e}"))?;
        if !metadata.file_type().is_file()
            || metadata.len() > MAX_ASSET
            || path.canonicalize().map_err(|e| e.to_string())?.parent()
                != Some(self.assets.as_path())
        {
            return Err("asset is not a regular file within the package directory".to_owned());
        }
        Ok(path)
    }
}

impl Port for GitHub {
    fn local_assets(&mut self) -> Result<Vec<Asset>, String> {
        let mut assets = Vec::new();
        for entry in fs::read_dir(&self.assets).map_err(|e| e.to_string())? {
            let name = entry
                .map_err(|e| e.to_string())?
                .file_name()
                .into_string()
                .map_err(|_| "asset name is not UTF-8".to_owned())?;
            let path = self.local_path(&name)?;
            let before = fs::metadata(&path).map_err(|e| e.to_string())?;
            let (size, digest) = fingerprint_reader(File::open(&path).map_err(|e| e.to_string())?)?;
            let after = fs::metadata(&path).map_err(|e| e.to_string())?;
            if size != before.len()
                || size != after.len()
                || before.modified().ok() != after.modified().ok()
            {
                return Err(format!("local asset changed while hashing: {name}"));
            }
            assets.push(Asset { name, size, digest });
        }
        assets.sort_by(|a, b| a.name.cmp(&b.name));
        Ok(assets)
    }

    fn verify_identity(&mut self, repo: &str, tag: &str, sha: &str) -> Result<(), String> {
        if repo != self.repo || tag != self.tag {
            return Err("publisher identity changed".to_owned());
        }
        Self::check_identity(repo, tag, sha)
    }

    fn current_release(&mut self, tag: &str) -> Result<Option<Release>, String> {
        let pages: Vec<Vec<RemoteRelease>> =
            paginated(&format!("repos/{}/releases?per_page=100", self.repo))?;
        let mut matching = pages.into_iter().flatten().filter(|r| r.tag_name == tag);
        let Some(release) = matching.next() else {
            return Ok(None);
        };
        if matching.next().is_some() {
            return Err("multiple releases identify the tag".to_owned());
        }
        self.release(release.id, false).map(Some)
    }

    fn create_draft(&mut self, tag: &str, sha: &str) -> Result<u64, String> {
        let result = api(
            &format!("repos/{}/releases", self.repo),
            &[
                "--method",
                "POST",
                "-f",
                &format!("tag_name={tag}"),
                "-f",
                &format!("target_commitish={sha}"),
                "-f",
                &format!("name={tag}"),
                "-F",
                "draft=true",
            ],
        )?;
        let release: RemoteRelease = serde_json::from_slice(&result).map_err(|e| e.to_string())?;
        if release.id == 0 || !release.draft || release.tag_name != tag {
            return Err("draft creation returned another identity or state".to_owned());
        }
        Ok(release.id)
    }

    fn upload(&mut self, release_id: u64, name: &str) -> Result<(), String> {
        let path = self.local_path(name)?;
        let endpoint = format!(
            "{GITHUB_UPLOADS_ORIGIN}/repos/{}/releases/{release_id}/assets?name={name}",
            self.repo
        );
        let path = path.to_str().ok_or("asset path is not UTF-8")?;
        api(
            &endpoint,
            &[
                "--method",
                "POST",
                "-H",
                "Content-Type: application/octet-stream",
                "--input",
                path,
            ],
        )?;
        Ok(())
    }

    fn publish(&mut self, release_id: u64) -> Result<(), String> {
        api(
            &format!("repos/{}/releases/{release_id}", self.repo),
            &["--method", "PATCH", "-F", "draft=false"],
        )?;
        Ok(())
    }

    fn verify_published(
        &mut self,
        release_id: u64,
        tag: &str,
        expected: &[Asset],
    ) -> Result<(), String> {
        let release = self.release(release_id, true)?;
        let mut actual = release.assets;
        let mut expected = expected.to_vec();
        actual.sort_by(|a, b| a.name.cmp(&b.name));
        expected.sort_by(|a, b| a.name.cmp(&b.name));
        if release.draft || release.tag != tag || actual != expected {
            return Err(
                "published release identity, state, or asset bytes do not match".to_owned(),
            );
        }
        Ok(())
    }
}

fn valid_sha(sha: &str) -> bool {
    sha.len() == core::COMMIT_SHA_HEX_LENGTH
        && sha
            .bytes()
            .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
}

fn gh_api(endpoint: &str) -> Command {
    let mut command = Command::new("gh");
    command
        .env("GH_HOST", "github.com")
        .env("GH_PROMPT_DISABLED", "1")
        .args(["api", "--hostname", "github.com", endpoint])
        .stderr(Stdio::null());
    command
}

fn api(endpoint: &str, args: &[&str]) -> Result<Vec<u8>, String> {
    api_with_command(endpoint, api_command(endpoint, args))
}

fn api_with_command(endpoint: &str, mut command: Command) -> Result<Vec<u8>, String> {
    let mut child = command
        .stdout(Stdio::piped())
        .spawn()
        .map_err(|e| format!("start GitHub CLI: {e}"))?;
    let mut output = Vec::new();
    let result = child
        .stdout
        .take()
        .ok_or("missing GitHub response stream".to_owned())
        .and_then(|stdout| {
            stdout
                .take(MAX_JSON + 1)
                .read_to_end(&mut output)
                .map_err(|e| e.to_string())
        });
    let failure = match result {
        Err(error) => Some(format!("GitHub response could not be read: {error}")),
        Ok(_) if output.len() as u64 > MAX_JSON => {
            Some("GitHub response exceeded its bound".to_owned())
        }
        Ok(_) => None,
    };
    if let Some(failure) = failure {
        return Err(format!("{failure}; {}", stop_child(&mut child)));
    }
    let status = child.wait().map_err(|e| e.to_string())?;
    if !status.success() {
        return Err(format!(
            "GitHub CLI request {endpoint} failed with status {status}"
        ));
    }
    Ok(output)
}

fn stop_child(child: &mut Child) -> String {
    let pid = child.id();
    match child.kill() {
        Ok(()) => match child.wait() {
            Ok(status) => format!("child {pid} terminated and reaped ({status})"),
            Err(error) => format!("child {pid} terminated but could not be reaped: {error}"),
        },
        Err(termination) => match child.try_wait() {
            Ok(Some(status)) => {
                format!("child {pid} already ended ({status}); termination reported {termination}")
            }
            Ok(None) => format!("child {pid} still running; termination failed: {termination}"),
            Err(status) => format!(
                "child {pid} status unavailable: {status}; termination failed: {termination}"
            ),
        },
    }
}

fn api_command(endpoint: &str, args: &[&str]) -> Command {
    let mut command = gh_api(endpoint);
    if !args.contains(&"--method") {
        command.args(["--method", "GET"]);
    }
    command.args(args);
    command
}

fn json<T: serde::de::DeserializeOwned>(endpoint: &str) -> Result<T, String> {
    serde_json::from_slice(&api(endpoint, &[])?).map_err(|e| format!("GitHub response: {e}"))
}

fn paginated<T: serde::de::DeserializeOwned>(endpoint: &str) -> Result<T, String> {
    serde_json::from_slice(&api(endpoint, &["--paginate", "--slurp"])?)
        .map_err(|e| format!("GitHub pagination: {e}"))
}

fn fingerprint_reader(mut input: impl Read) -> Result<(u64, Digest), String> {
    let mut hasher = Sha256::new();
    let mut size = 0_u64;
    let mut buffer = [0_u8; 65536];
    loop {
        let count = input.read(&mut buffer).map_err(|e| e.to_string())?;
        if count == 0 {
            break;
        }
        size = size
            .checked_add(count as u64)
            .ok_or("asset size overflow")?;
        if size > MAX_ASSET {
            return Err("asset exceeds the supported two-GiB bound".to_owned());
        }
        hasher.update(&buffer[..count]);
    }
    Ok((size, hasher.finalize().into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs::OpenOptions;
    use std::io::{BufRead, BufReader, Read, Write};
    use std::net::{SocketAddr, TcpListener, TcpStream};
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::thread::{self, JoinHandle};
    use std::time::Duration;

    static NEXT_TEMP_FILE: AtomicU64 = AtomicU64::new(0);

    struct TempFile(PathBuf);

    impl Drop for TempFile {
        fn drop(&mut self) {
            let _ = fs::remove_file(&self.0);
        }
    }

    struct FixtureResponse {
        status: &'static str,
        headers: Vec<(String, String)>,
        body: Vec<u8>,
    }

    struct FixtureRequest {
        line: String,
        headers: String,
        body: Vec<u8>,
    }

    fn fixture_response(status: &'static str, body: &[u8]) -> FixtureResponse {
        FixtureResponse {
            status,
            headers: Vec::new(),
            body: body.to_vec(),
        }
    }

    fn fixture_command(endpoint: &str, args: &[&str]) -> Command {
        let mut command = api_command(endpoint, args);
        command
            .env("GH_TOKEN", "fixture-gh-token")
            .env("GITHUB_TOKEN", "fixture-github-token")
            .env("GH_ENTERPRISE_TOKEN", "fixture-gh-enterprise-token")
            .env("GITHUB_ENTERPRISE_TOKEN", "fixture-github-enterprise-token");
        command
    }

    fn fixture_server(
        responses: Vec<FixtureResponse>,
    ) -> (SocketAddr, JoinHandle<Vec<FixtureRequest>>) {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind local HTTP fixture");
        listener
            .set_nonblocking(true)
            .expect("set bounded fixture accept");
        let address = listener.local_addr().expect("read fixture address");
        let server = thread::spawn(move || {
            responses
                .into_iter()
                .map(|response| {
                    let mut stream = accept_fixture(&listener);
                    let request = read_fixture_request(&mut stream);
                    write_fixture_response(&mut stream, &response);
                    request
                })
                .collect()
        });
        (address, server)
    }

    fn accept_fixture(listener: &TcpListener) -> TcpStream {
        for _ in 0..1000 {
            match listener.accept() {
                Ok((stream, _)) => {
                    stream
                        .set_nonblocking(false)
                        .expect("use blocking fixture streams");
                    stream
                        .set_read_timeout(Some(Duration::from_secs(10)))
                        .expect("bound fixture read");
                    stream
                        .set_write_timeout(Some(Duration::from_secs(10)))
                        .expect("bound fixture write");
                    return stream;
                }
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    thread::sleep(Duration::from_millis(10));
                }
                Err(error) => panic!("accept local HTTP fixture: {error}"),
            }
        }
        panic!("local HTTP fixture received no request within its fixed retry bound");
    }

    fn read_fixture_request(stream: &mut TcpStream) -> FixtureRequest {
        let mut reader = BufReader::new(stream);
        let mut line = Vec::new();
        reader
            .read_until(b'\n', &mut line)
            .expect("read fixture request line");
        let request_line = String::from_utf8(line).expect("fixture request line is UTF-8");
        let mut header_lines = Vec::new();
        loop {
            let mut header = Vec::new();
            reader
                .read_until(b'\n', &mut header)
                .expect("read fixture request header");
            if header == b"\r\n" {
                break;
            }
            header_lines.push(String::from_utf8(header).expect("fixture header is UTF-8"));
            assert!(header_lines.len() <= 64, "fixture has bounded header count");
        }
        let headers = header_lines.concat();
        let chunked = header_lines.iter().any(|header| {
            header.split_once(':').is_some_and(|(name, value)| {
                name.eq_ignore_ascii_case("transfer-encoding")
                    && value.trim().eq_ignore_ascii_case("chunked")
            })
        });
        let body = if chunked {
            read_chunked_fixture_body(&mut reader)
        } else {
            let length = header_lines
                .iter()
                .find_map(|header| {
                    header.split_once(':').and_then(|(name, value)| {
                        name.eq_ignore_ascii_case("content-length")
                            .then(|| value.trim().parse::<usize>().expect("valid content length"))
                    })
                })
                .unwrap_or(0);
            assert!(length <= MAX_ASSET as usize, "fixture body stays bounded");
            let mut body = vec![0; length];
            reader
                .read_exact(&mut body)
                .expect("read bounded fixture request body");
            body
        };
        FixtureRequest {
            line: request_line.trim_end().to_owned(),
            headers,
            body,
        }
    }

    fn read_chunked_fixture_body(reader: &mut impl BufRead) -> Vec<u8> {
        let mut body = Vec::new();
        loop {
            let mut size_line = String::new();
            reader
                .read_line(&mut size_line)
                .expect("read fixture chunk size");
            let size = usize::from_str_radix(
                size_line
                    .trim_end()
                    .split(';')
                    .next()
                    .expect("chunk size is present"),
                16,
            )
            .expect("fixture chunk size is hexadecimal");
            assert!(body.len().saturating_add(size) <= MAX_ASSET as usize);
            if size == 0 {
                loop {
                    let mut trailer = String::new();
                    reader
                        .read_line(&mut trailer)
                        .expect("read fixture chunk trailer");
                    if trailer == "\r\n" {
                        return body;
                    }
                }
            }
            let start = body.len();
            body.resize(start + size, 0);
            reader
                .read_exact(&mut body[start..])
                .expect("read fixture chunk bytes");
            let mut separator = [0; 2];
            reader
                .read_exact(&mut separator)
                .expect("read fixture chunk separator");
            assert_eq!(separator, *b"\r\n");
        }
    }

    fn write_fixture_response(stream: &mut TcpStream, response: &FixtureResponse) {
        let mut headers = format!(
            "HTTP/1.1 {}\r\nContent-Length: {}\r\nConnection: close\r\n",
            response.status,
            response.body.len()
        );
        for (name, value) in &response.headers {
            headers.push_str(name);
            headers.push_str(": ");
            headers.push_str(value);
            headers.push_str("\r\n");
        }
        headers.push_str("\r\n");
        stream
            .write_all(headers.as_bytes())
            .expect("write fixture response headers");
        stream
            .write_all(&response.body)
            .or_else(|error| {
                if matches!(
                    error.kind(),
                    std::io::ErrorKind::BrokenPipe | std::io::ErrorKind::ConnectionReset
                ) {
                    Ok(())
                } else {
                    Err(error)
                }
            })
            .expect("write fixture response body");
    }

    fn fixture_file(bytes: &[u8]) -> TempFile {
        let path = std::env::temp_dir().join(format!(
            "snaply-gh-api-{}-{}.bin",
            std::process::id(),
            NEXT_TEMP_FILE.fetch_add(1, Ordering::Relaxed)
        ));
        let mut file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .expect("create unique fixture asset");
        file.write_all(bytes).expect("write fixture asset bytes");
        TempFile(path)
    }

    #[test]
    fn hashes_actual_bytes_with_a_published_sha256_vector() {
        let (size, digest) = fingerprint_reader(std::io::Cursor::new(b"abc")).unwrap();
        assert_eq!(size, 3);
        assert_eq!(
            digest,
            [
                0xba, 0x78, 0x16, 0xbf, 0x8f, 0x01, 0xcf, 0xea, 0x41, 0x41, 0x40, 0xde, 0x5d, 0xae,
                0x22, 0x23, 0xb0, 0x03, 0x61, 0xa3, 0x96, 0x17, 0x7a, 0x9c, 0xb4, 0x10, 0xff, 0x61,
                0xf2, 0x00, 0x15, 0xad
            ]
        );
    }

    #[test]
    fn distinguishes_valid_empty_pages_from_malformed_or_missing_api_fields() {
        let pages = serde_json::from_str::<Vec<Vec<RemoteRelease>>>("[[]]").unwrap();
        assert_eq!(pages.len(), 1);
        assert!(pages[0].is_empty());
        assert!(
            serde_json::from_str::<Vec<Vec<RemoteRelease>>>("{\"message\":\"Bad credentials\"}")
                .is_err()
        );
        assert!(
            serde_json::from_str::<RemoteRelease>("{\"id\":3,\"tag_name\":\"v1.2.3\"}").is_err()
        );
        assert!(
            serde_json::from_str::<RemoteAsset>("{\"id\":4,\"name\":\"LICENSE\",\"size\":1}")
                .is_err()
        );
    }

    #[test]
    fn command_uses_fixed_host_api_and_has_no_mutation_by_default() {
        let command = api_command("repos/P4suta/Snaply/releases", &[]);
        let args: Vec<_> = command.get_args().map(|s| s.to_str().unwrap()).collect();
        assert_eq!(
            args,
            [
                "api",
                "--hostname",
                "github.com",
                "repos/P4suta/Snaply/releases",
                "--method",
                "GET"
            ]
        );
    }

    #[test]
    fn mutations_use_one_explicit_method_and_preserve_the_upload_host() {
        let endpoint =
            "https://uploads.github.com/repos/P4suta/Snaply/releases/4/assets?name=LICENSE";
        for method in ["POST", "PATCH"] {
            let command = api_command(
                endpoint,
                &["--method", method, "--input", "package/LICENSE"],
            );
            let args: Vec<_> = command.get_args().map(|s| s.to_str().unwrap()).collect();
            assert_eq!(args.iter().filter(|arg| **arg == "--method").count(), 1);
            assert!(args.windows(2).any(|pair| pair == ["--method", method]));
            assert!(args.contains(&endpoint));
            assert!(!args.contains(&"--clobber"));
        }
    }

    #[test]
    fn prepared_gh_command_uploads_exact_binary_bytes_to_local_http_only() {
        let bytes = [0, 0xff, b'\r', b'\n', 0x80, 0x00, 0x7f];
        let asset = fixture_file(&bytes);
        let (address, server) = fixture_server(vec![fixture_response("200 OK", b"{\"ok\":true}")]);
        let endpoint =
            format!("http://{address}/repos/P4suta/Snaply/releases/4/assets?name=LICENSE");
        let result = api_with_command(
            &endpoint,
            fixture_command(
                &endpoint,
                &[
                    "--method",
                    "POST",
                    "-H",
                    "Content-Type: application/octet-stream",
                    "--input",
                    asset.0.to_str().expect("fixture path is UTF-8"),
                ],
            ),
        )
        .expect("local fixture upload succeeds");
        assert_eq!(result, b"{\"ok\":true}");
        let requests = server.join().expect("fixture server completes");
        assert_eq!(requests.len(), 1);
        assert!(
            requests[0]
                .line
                .starts_with("POST /repos/P4suta/Snaply/releases/4/assets?name=LICENSE ")
        );
        assert!(
            requests[0]
                .headers
                .to_ascii_lowercase()
                .contains("content-type: application/octet-stream\r\n")
        );
        assert_eq!(requests[0].body, bytes);
        assert!(
            requests[0]
                .headers
                .to_ascii_lowercase()
                .contains("content-length: 7\r\n")
        );
    }

    #[test]
    fn prepared_gh_command_sends_patch_fields_as_json() {
        let (address, server) = fixture_server(vec![fixture_response(
            "200 OK",
            b"{\"id\":4,\"draft\":false}",
        )]);
        let endpoint = format!("http://{address}/repos/P4suta/Snaply/releases/4");
        let result = api_with_command(
            &endpoint,
            fixture_command(&endpoint, &["--method", "PATCH", "-F", "draft=false"]),
        )
        .expect("local fixture patch succeeds");
        assert_eq!(result, b"{\"id\":4,\"draft\":false}");
        let requests = server.join().expect("fixture server completes");
        assert_eq!(requests.len(), 1);
        assert!(
            requests[0]
                .line
                .starts_with("PATCH /repos/P4suta/Snaply/releases/4 ")
        );
        assert!(
            requests[0]
                .headers
                .to_ascii_lowercase()
                .contains("content-type: application/json")
        );
        assert_eq!(
            serde_json::from_slice::<serde_json::Value>(&requests[0].body).expect("JSON body"),
            serde_json::json!({"draft": false})
        );
    }

    #[test]
    fn gh_paginate_slurp_preserves_page_boundaries() {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind local HTTP fixture");
        listener
            .set_nonblocking(true)
            .expect("set bounded fixture accept");
        let address = listener.local_addr().expect("read fixture address");
        let next = format!("http://{address}/repos/P4suta/Snaply/releases?page=2");
        let responses = vec![
            FixtureResponse {
                status: "200 OK",
                headers: vec![("Link".to_owned(), format!("<{next}>; rel=\"next\""))],
                body: b"[{\"id\":1,\"tag_name\":\"v1.0.0\",\"draft\":true}]".to_vec(),
            },
            fixture_response(
                "200 OK",
                b"[{\"id\":2,\"tag_name\":\"v2.0.0\",\"draft\":true}]",
            ),
        ];
        let server = thread::spawn(move || {
            responses
                .into_iter()
                .map(|response| {
                    let mut stream = accept_fixture(&listener);
                    stream
                        .set_read_timeout(Some(Duration::from_secs(10)))
                        .expect("bound fixture read");
                    stream
                        .set_write_timeout(Some(Duration::from_secs(10)))
                        .expect("bound fixture write");
                    let request = read_fixture_request(&mut stream);
                    write_fixture_response(&mut stream, &response);
                    request
                })
                .collect::<Vec<_>>()
        });
        let endpoint = format!("http://{address}/repos/P4suta/Snaply/releases?per_page=100");
        let output = api_with_command(
            &endpoint,
            fixture_command(&endpoint, &["--paginate", "--slurp"]),
        )
        .expect("local paginated fixture succeeds");
        let requests = server.join().expect("fixture server completes");
        assert_eq!(requests.len(), 2);
        assert!(
            requests[0]
                .line
                .starts_with("GET /repos/P4suta/Snaply/releases?per_page=100 ")
        );
        assert!(
            requests[1]
                .line
                .starts_with("GET /repos/P4suta/Snaply/releases?page=2 ")
        );
        let pages = serde_json::from_slice::<Vec<Vec<RemoteRelease>>>(&output)
            .expect("gh --paginate --slurp returns an array of pages");
        assert_eq!(pages.len(), 2);
        assert_eq!(pages[0][0].id, 1);
        assert_eq!(pages[1][0].id, 2);
    }

    #[test]
    fn gh_http_error_with_empty_body_is_not_a_successful_empty_response() {
        let (address, server) = fixture_server(vec![fixture_response("401 Unauthorized", b"")]);
        let endpoint = format!("http://{address}/repos/P4suta/Snaply/releases");
        let result = api_with_command(&endpoint, fixture_command(&endpoint, &[]));
        assert!(result.is_err());
        let requests = server.join().expect("fixture server completes");
        assert_eq!(requests.len(), 1);
        assert!(
            requests[0]
                .line
                .starts_with("GET /repos/P4suta/Snaply/releases ")
        );
    }

    #[test]
    fn gh_response_above_json_bound_is_rejected() {
        let mut body = vec![b'"'];
        body.resize(MAX_JSON as usize + 1, b'a');
        body.push(b'"');
        let (address, server) = fixture_server(vec![fixture_response("200 OK", &body)]);
        let endpoint = format!("http://{address}/large");
        let result = api_with_command(&endpoint, fixture_command(&endpoint, &[]));
        assert!(
            result
                .expect_err("oversized output is rejected")
                .contains("exceeded its bound")
        );
        let requests = server.join().expect("fixture server completes");
        assert_eq!(requests.len(), 1);
    }
}
