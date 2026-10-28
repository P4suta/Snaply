use snaply_release_publisher::{core, engine, github::GitHub};
use std::path::PathBuf;

const HELP: &str = "snaply-release-publisher --repo P4suta/Snaply --tag vMAJOR.MINOR.PATCH --sha LOWERCASE_SHA [--assets DIRECTORY] [--apply | --check-identity]\nDefault: print a read-only publication plan; --assets is required.\n--apply: create or resume a verified draft, upload missing bytes, and publish.\n--check-identity: only verify the remote tag, main ancestry, and version.\nPublished targets, changed bytes, uncertain writes, and mismatched assets fail.\nNo asset replacement, deletion, tag creation, or automatic retry is supported.";

fn main() {
    if let Err(error) = execute() {
        eprintln!(
            "{}",
            serde_json::json!({"status":"incomplete", "error":error})
        );
        std::process::exit(1);
    }
}

fn execute() -> Result<(), String> {
    let mut args = std::env::args_os()
        .skip(1)
        .map(|arg| {
            arg.into_string()
                .map_err(|_| "arguments must be UTF-8".to_owned())
        })
        .collect::<Result<Vec<_>, _>>()?
        .into_iter();
    let mut repo = None;
    let mut tag = None;
    let mut sha = None;
    let mut assets = None;
    let mut apply = false;
    let mut check_identity = false;
    while let Some(arg) = args.next() {
        match arg.as_str() {
            "--help" | "-h" => {
                println!("{HELP}");
                return Ok(());
            }
            "--repo" if repo.is_none() => repo = Some(args.next().ok_or("--repo needs a value")?),
            "--tag" if tag.is_none() => tag = Some(args.next().ok_or("--tag needs a value")?),
            "--sha" if sha.is_none() => sha = Some(args.next().ok_or("--sha needs a value")?),
            "--assets" if assets.is_none() => {
                assets = Some(PathBuf::from(args.next().ok_or("--assets needs a value")?))
            }
            "--apply" if !apply => apply = true,
            "--check-identity" if !check_identity => check_identity = true,
            _ => return Err(format!("unknown or duplicate argument: {arg}")),
        }
    }
    let repo = repo.ok_or("--repo is required")?;
    let tag = tag.ok_or("--tag is required")?;
    let sha = sha.ok_or("--sha is required")?;
    core::validate_identity(&repo, &tag, &sha)?;
    if repo != "P4suta/Snaply" {
        return Err("publisher is scoped to P4suta/Snaply".to_owned());
    }
    if check_identity {
        if apply || assets.is_some() {
            return Err("--check-identity cannot publish or inspect package assets".to_owned());
        }
        GitHub::check_identity(&repo, &tag, &sha)?;
        println!(
            "{}",
            serde_json::json!({"status":"identity-verified", "repo":repo,"tag":tag,"sha":sha})
        );
        return Ok(());
    }
    let mut port = GitHub::new(
        &repo,
        &tag,
        &assets.ok_or("--assets is required for plan or apply")?,
    )?;
    let result = engine::run(&mut port, &repo, &tag, &sha, apply)?;
    println!(
        "{}",
        serde_json::json!({"status":if result.published {"published"} else {"plan"},"repo":repo,"tag":tag,"sha":sha,"release_id":result.plan.release_id,"missing":result.plan.missing})
    );
    Ok(())
}
