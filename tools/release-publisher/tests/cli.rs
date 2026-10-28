use std::process::Command;

fn run(args: &[&str]) -> std::process::Output {
    Command::new(env!("CARGO_BIN_EXE_snaply-release-publisher"))
        .args(args)
        .env("GH_PROMPT_DISABLED", "1")
        .output()
        .unwrap()
}

#[test]
fn help_describes_read_only_default_and_explicit_publication() {
    let output = run(&["--help"]);
    assert!(output.status.success());
    let help = String::from_utf8(output.stdout).unwrap();
    assert!(help.contains("read-only publication plan"));
    assert!(help.contains("--apply"));
    assert!(help.contains("No asset replacement"));
}

#[test]
fn refuses_malformed_or_conflicting_arguments_before_any_github_request() {
    for args in [
        vec![],
        vec!["--apply"],
        vec![
            "--repo",
            "another/repository",
            "--tag",
            "v1.2.3",
            "--sha",
            "0123456789abcdef0123456789abcdef01234567",
        ],
        vec![
            "--repo",
            "P4suta/Snaply",
            "--tag",
            "v1.2.3",
            "--sha",
            "INVALID",
        ],
        vec![
            "--repo",
            "P4suta/Snaply",
            "--tag",
            "v1.2.3",
            "--sha",
            "0123456789abcdef0123456789abcdef01234567",
            "--check-identity",
            "--apply",
        ],
        vec!["--repo", "P4suta/Snaply", "--repo", "P4suta/Snaply"],
    ] {
        let output = run(&args);
        assert!(!output.status.success(), "{args:?}");
        assert!(output.stdout.is_empty());
        let error: serde_json::Value = serde_json::from_slice(&output.stderr).unwrap();
        assert_eq!(error["status"], "incomplete");
        assert!(!error["error"].as_str().unwrap().contains("GitHub CLI"));
    }
}

#[cfg(unix)]
#[test]
fn refuses_non_utf8_arguments_with_a_structured_error() {
    use std::os::unix::ffi::OsStringExt;
    let output = Command::new(env!("CARGO_BIN_EXE_snaply-release-publisher"))
        .arg(std::ffi::OsString::from_vec(vec![0xff]))
        .output()
        .unwrap();
    assert!(!output.status.success());
    let error: serde_json::Value = serde_json::from_slice(&output.stderr).unwrap();
    assert_eq!(error["status"], "incomplete");
    assert_eq!(error["error"], "arguments must be UTF-8");
}
