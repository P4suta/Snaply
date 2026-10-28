pub type Digest = [u8; 32];
pub const COMMIT_SHA_HEX_LENGTH: usize = 40;
const VERSION_COMPONENTS: usize = 3;

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Asset {
    pub name: String,
    pub size: u64,
    pub digest: Digest,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Release {
    pub id: u64,
    pub tag: String,
    pub draft: bool,
    pub assets: Vec<Asset>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Plan {
    pub release_id: Option<u64>,
    pub missing: Vec<String>,
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AssetDecision {
    Upload,
    Reuse,
    Reject,
}

fn valid_tag(tag: &str) -> bool {
    let Some(version) = tag.strip_prefix('v') else {
        return false;
    };
    let components: Vec<_> = version.split('.').collect();
    components.len() == VERSION_COMPONENTS
        && components.iter().all(|component| {
            !component.is_empty()
                && component.bytes().all(|byte| byte.is_ascii_digit())
                && (component.len() == 1 || !component.starts_with('0'))
        })
}

pub fn validate_identity(repo: &str, tag: &str, sha: &str) -> Result<(), String> {
    let components: Vec<_> = repo.split('/').collect();
    if components.len() != 2
        || !components.iter().all(|component| {
            !component.is_empty()
                && !matches!(*component, "." | "..")
                && component
                    .bytes()
                    .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_'))
        })
    {
        return Err("repository must be an ASCII owner/name identity".into());
    }
    if !valid_tag(tag) {
        return Err("tag must be canonical vMAJOR.MINOR.PATCH".into());
    }
    if sha.len() != COMMIT_SHA_HEX_LENGTH
        || !sha
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte))
    {
        return Err("commit must be exactly 40 lowercase hexadecimal characters".into());
    }
    Ok(())
}

pub fn expected_names(tag: &str) -> Vec<String> {
    vec![
        format!("snaply-{tag}-win-x64.zip"),
        format!("snaply-{tag}-win-arm64.zip"),
        format!("snaply-{tag}.msixbundle"),
        "LICENSE".into(),
        "NOTICE".into(),
        "dependency-licenses.txt".into(),
        "SHA256SUMS.txt".into(),
        "snaply.spdx.json".into(),
    ]
}

pub fn plan(tag: &str, expected: &[Asset], current: Option<&Release>) -> Result<Plan, String> {
    if !valid_tag(tag) {
        return Err("tag must be canonical vMAJOR.MINOR.PATCH".into());
    }
    let names = expected_names(tag);
    if expected.len() != names.len() {
        return Err("local payload must contain exactly the eight release assets".into());
    }
    let mut local = std::collections::BTreeMap::new();
    for asset in expected {
        if !names.contains(&asset.name) || local.insert(asset.name.as_str(), asset).is_some() {
            return Err(format!(
                "unexpected or duplicate local asset: {}",
                asset.name
            ));
        }
    }
    let mut remote = std::collections::BTreeMap::new();
    if let Some(release) = current {
        if release.id == 0 || release.tag != tag || !release.draft {
            return Err("target must be an identified draft for the exact tag".into());
        }
        for asset in &release.assets {
            if !local.contains_key(asset.name.as_str())
                || remote.insert(asset.name.as_str(), asset).is_some()
            {
                return Err(format!(
                    "unexpected or duplicate remote asset: {}",
                    asset.name
                ));
            }
        }
    }
    let mut missing = Vec::new();
    for name in names {
        let expected = local[name.as_str()];
        let decision = match remote.get(name.as_str()) {
            Some(asset) => asset_decision(
                true,
                true,
                asset.size == expected.size,
                asset.digest == expected.digest,
            ),
            None => asset_decision(false, false, false, false),
        };
        match decision {
            AssetDecision::Upload => missing.push(name),
            AssetDecision::Reuse => {}
            AssetDecision::Reject => return Err(format!("remote asset bytes differ: {name}")),
        }
    }
    Ok(Plan {
        release_id: current.map(|release| release.id),
        missing,
    })
}

pub const fn asset_decision(
    present: bool,
    uploaded: bool,
    same_size: bool,
    same_digest: bool,
) -> AssetDecision {
    if !present {
        AssetDecision::Upload
    } else if uploaded && same_size && same_digest {
        AssetDecision::Reuse
    } else {
        AssetDecision::Reject
    }
}

pub const fn can_publish(draft: bool, identity_verified: bool, missing: usize) -> bool {
    draft && identity_verified && missing == 0
}

#[cfg(kani)]
mod proofs {
    use super::*;

    const TAG: &str = "v1.2.3";

    fn canonical_assets() -> Vec<Asset> {
        let names = expected_names(TAG);
        assert_eq!(
            names,
            [
                "snaply-v1.2.3-win-x64.zip",
                "snaply-v1.2.3-win-arm64.zip",
                "snaply-v1.2.3.msixbundle",
                "LICENSE",
                "NOTICE",
                "dependency-licenses.txt",
                "SHA256SUMS.txt",
                "snaply.spdx.json",
            ]
        );
        names
            .into_iter()
            .map(|name| Asset {
                name,
                size: 1,
                digest: [1; 32],
            })
            .collect()
    }

    fn draft(assets: Vec<Asset>) -> Release {
        Release {
            id: 42,
            tag: TAG.into(),
            draft: true,
            assets,
        }
    }

    #[kani::proof]
    #[kani::unwind(128)]
    fn plan_reconciles_every_partial_draft() {
        let mask: u8 = kani::any();
        let reverse: bool = kani::any();
        let mut expected = canonical_assets();
        let mut present = Vec::new();
        let mut missing = Vec::new();
        for (index, asset) in expected.iter().enumerate() {
            if mask & (1 << index) != 0 {
                present.push(asset.clone());
            } else {
                missing.push(asset.name.clone());
            }
        }
        if reverse {
            expected.reverse();
            present.reverse();
        }
        let current = draft(present);
        let actual = plan(TAG, &expected, Some(&current)).unwrap();
        assert_eq!(actual.release_id, Some(42));
        assert_eq!(actual.missing, missing);
        let new_release = plan(TAG, &expected, None).unwrap();
        assert_eq!(new_release.release_id, None);
        assert_eq!(new_release.missing, expected_names(TAG));
        kani::cover!(mask == 0 && actual.missing.len() == 8);
        kani::cover!(mask == u8::MAX && actual.missing.is_empty());
        kani::cover!(mask != 0 && mask != u8::MAX && !actual.missing.is_empty());
        kani::cover!(reverse);
    }

    #[kani::proof]
    #[kani::unwind(128)]
    fn plan_rejects_arbitrary_asset_byte_mismatches() {
        let index: u8 = kani::any();
        kani::assume(index < 8);
        let size: u64 = kani::any();
        let digest: Digest = kani::any();
        let expected = canonical_assets();
        let mut current = draft(expected.clone());
        current.assets[usize::from(index)].size = size;
        current.assets[usize::from(index)].digest = digest;
        let same_size = size == 1;
        let same_digest = digest == [1; 32];
        let actual = plan(TAG, &expected, Some(&current));
        assert_eq!(actual.is_ok(), same_size && same_digest);
        if let Ok(plan) = actual {
            assert_eq!(plan.release_id, Some(42));
            assert!(plan.missing.is_empty());
        }
        kani::cover!(same_size && same_digest);
        kani::cover!(!same_size && same_digest);
        kani::cover!(same_size && !same_digest);
        kani::cover!(!same_size && !same_digest);
    }

    #[kani::proof]
    #[kani::unwind(128)]
    fn plan_refuses_invalid_release_inventory() {
        let index: u8 = kani::any();
        let defect: u8 = kani::any();
        kani::assume(index < 8 && defect < 8);
        let mut expected = canonical_assets();
        let mut current = draft(expected.clone());
        let index = usize::from(index);
        match defect {
            0 => {
                expected.remove(index);
            }
            1 => {
                expected[index] = expected[(index + 1) % 8].clone();
            }
            2 => {
                expected[index].name = "unexpected.txt".into();
            }
            3 => {
                current.assets.push(current.assets[index].clone());
            }
            4 => {
                current.assets[index].name = "unexpected.txt".into();
            }
            5 => {
                current.id = 0;
            }
            6 => {
                current.tag = "v1.2.4".into();
            }
            7 => {
                current.draft = false;
            }
            _ => unreachable!(),
        }
        assert!(plan(TAG, &expected, Some(&current)).is_err());
        kani::cover!(defect == 0);
        kani::cover!(defect == 1);
        kani::cover!(defect == 2);
        kani::cover!(defect == 3);
        kani::cover!(defect == 4);
        kani::cover!(defect == 5);
        kani::cover!(defect == 6);
        kani::cover!(defect == 7);
    }

    #[kani::proof]
    #[kani::unwind(128)]
    fn identity_accepts_exactly_lowercase_hex_sha_bytes() {
        let bytes: [u8; 40] = kani::any();
        let lowercase_hex = bytes
            .iter()
            .all(|byte| matches!(byte, b'0'..=b'9' | b'a'..=b'f'));
        let utf8 = std::str::from_utf8(&bytes);
        let accepted = match utf8 {
            Ok(sha) => validate_identity("P4suta/Snaply", TAG, sha).is_ok(),
            Err(_) => false,
        };
        assert_eq!(accepted, lowercase_hex);
        kani::cover!(accepted);
        kani::cover!(!accepted && utf8.is_ok());
        kani::cover!(!accepted && utf8.is_err());
    }

    #[kani::proof]
    #[kani::unwind(1)]
    fn asset_policy_covers_all_inputs() {
        let present: bool = kani::any();
        let uploaded: bool = kani::any();
        let same_size: bool = kani::any();
        let same_digest: bool = kani::any();
        let decision = asset_decision(present, uploaded, same_size, same_digest);
        assert_eq!(decision == AssetDecision::Upload, !present);
        assert_eq!(
            decision == AssetDecision::Reuse,
            present && uploaded && same_size && same_digest
        );
        assert_eq!(
            decision == AssetDecision::Reject,
            present && !(uploaded && same_size && same_digest)
        );
        kani::cover!(decision == AssetDecision::Upload);
        kani::cover!(decision == AssetDecision::Reuse);
        kani::cover!(decision == AssetDecision::Reject && !uploaded);
        kani::cover!(decision == AssetDecision::Reject && uploaded && !same_size);
        kani::cover!(decision == AssetDecision::Reject && uploaded && same_size && !same_digest);
    }

    #[kani::proof]
    #[kani::unwind(1)]
    fn publication_requires_complete_verified_draft() {
        let draft: bool = kani::any();
        let identity_verified: bool = kani::any();
        let missing: usize = kani::any();
        let permitted = can_publish(draft, identity_verified, missing);
        assert_eq!(permitted, draft && identity_verified && missing == 0);
        kani::cover!(permitted);
        kani::cover!(!permitted && !draft && identity_verified && missing == 0);
        kani::cover!(!permitted && draft && !identity_verified && missing == 0);
        kani::cover!(!permitted && draft && identity_verified && missing > 0);
    }

    #[kani::proof]
    #[kani::unwind(1)]
    #[kani::should_panic]
    fn reject_reusing_mismatched_asset() {
        assert_eq!(
            asset_decision(true, true, true, false),
            AssetDecision::Reuse
        );
    }

    #[kani::proof]
    #[kani::unwind(1)]
    #[kani::should_panic]
    fn reject_publishing_incomplete_draft() {
        assert!(can_publish(true, true, 1));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const TAG: &str = "v1.2.3";
    const SHA: &str = "0123456789abcdef0123456789abcdef01234567";
    const NAMES: [&str; 8] = [
        "snaply-v1.2.3-win-x64.zip",
        "snaply-v1.2.3-win-arm64.zip",
        "snaply-v1.2.3.msixbundle",
        "LICENSE",
        "NOTICE",
        "dependency-licenses.txt",
        "SHA256SUMS.txt",
        "snaply.spdx.json",
    ];

    fn assets() -> Vec<Asset> {
        NAMES
            .iter()
            .zip(1_u8..)
            .map(|(name, byte)| Asset {
                name: (*name).into(),
                size: u64::from(byte),
                digest: [byte; 32],
            })
            .collect()
    }

    fn release(assets: Vec<Asset>) -> Release {
        Release {
            id: 42,
            tag: TAG.into(),
            draft: true,
            assets,
        }
    }

    #[test]
    fn accepts_canonical_identity_without_numeric_overflow() {
        for (repo, tag) in [
            ("P4suta/Snaply", TAG),
            ("owner-name/repo_name.v2", "v0.0.0"),
            ("a/b", "v999999999999999999999999999999.2.3"),
        ] {
            assert_eq!(validate_identity(repo, tag, SHA), Ok(()));
        }
    }

    #[test]
    fn rejects_malformed_identity_components() {
        for repo in [
            "",
            "owner",
            "/repo",
            "owner/",
            "a/b/c",
            "a/..",
            "./b",
            "a/b c",
            "a/b\n",
            "所有者/repo",
            "a/b%2fc",
        ] {
            assert!(validate_identity(repo, TAG, SHA).is_err(), "{repo:?}");
        }
        for tag in [
            "",
            "1.2.3",
            "V1.2.3",
            "v01.2.3",
            "v1.02.3",
            "v1.2.03",
            "v1.2",
            "v1.2.3.4",
            "v1.2.3-rc.1",
            "v1.2.3+build",
            "v+1.2.3",
            "v\u{661}.2.3",
            "v1.2.3\n",
        ] {
            assert!(validate_identity("a/b", tag, SHA).is_err(), "{tag:?}");
            assert!(plan(tag, &assets(), None).is_err(), "{tag:?}");
        }
        for sha in [
            "".to_string(),
            "a".repeat(39),
            "a".repeat(41),
            "A".repeat(40),
            "g".repeat(40),
            format!("{}\n", "a".repeat(39)),
        ] {
            assert!(validate_identity("a/b", TAG, &sha).is_err(), "{sha:?}");
        }
    }

    #[test]
    fn payload_names_include_license_checksum_and_sbom_assets() {
        assert_eq!(expected_names(TAG), NAMES);
        assert_eq!(expected_names("v0.0.0")[0], "snaply-v0.0.0-win-x64.zip");
    }

    #[test]
    fn absent_release_requires_every_asset_in_canonical_order() {
        let mut expected = assets();
        expected.reverse();
        let actual = plan(TAG, &expected, None).unwrap();
        assert_eq!(actual.release_id, None);
        assert_eq!(actual.missing, NAMES);
    }

    #[test]
    fn every_partial_draft_reuses_only_matching_assets() {
        let expected = assets();
        for mask in 0_u16..256 {
            let present = expected
                .iter()
                .enumerate()
                .filter(|(index, _)| mask & (1 << index) != 0)
                .map(|(_, asset)| asset.clone())
                .rev()
                .collect();
            let current = release(present);
            let actual = plan(TAG, &expected, Some(&current)).unwrap();
            let missing: Vec<_> = expected
                .iter()
                .enumerate()
                .filter(|(index, _)| mask & (1 << index) == 0)
                .map(|(_, asset)| asset.name.clone())
                .collect();
            assert_eq!(
                actual,
                Plan {
                    release_id: Some(42),
                    missing
                }
            );
        }
    }

    #[test]
    fn rejects_incomplete_duplicate_or_foreign_local_payloads() {
        let expected = assets();
        for index in 0..expected.len() {
            let mut missing = expected.clone();
            missing.remove(index);
            assert!(plan(TAG, &missing, None).is_err());
            let mut duplicate = expected.clone();
            duplicate.push(expected[index].clone());
            assert!(plan(TAG, &duplicate, None).is_err());
        }
        let mut duplicate = expected.clone();
        duplicate[1] = duplicate[0].clone();
        assert!(plan(TAG, &duplicate, None).is_err());
        for name in [
            "unexpected.txt",
            "../LICENSE",
            "license",
            "snaply-v1.2.4-win-x64.zip",
        ] {
            let mut foreign = expected.clone();
            foreign[0].name = name.into();
            assert!(plan(TAG, &foreign, None).is_err());
        }
    }

    #[test]
    fn rejects_published_or_wrong_release_identity() {
        let expected = assets();
        let mut current = release(Vec::new());
        current.id = 0;
        assert!(plan(TAG, &expected, Some(&current)).is_err());
        current.id = 42;
        current.tag = "v1.2.4".into();
        assert!(plan(TAG, &expected, Some(&current)).is_err());
        current.tag = TAG.into();
        current.draft = false;
        assert!(plan(TAG, &expected, Some(&current)).is_err());
        current.assets = expected.clone();
        assert!(plan(TAG, &expected, Some(&current)).is_err());
    }

    #[test]
    fn refuses_remote_duplicates_unknown_names_and_byte_changes() {
        let expected = assets();
        for index in 0..expected.len() {
            let mut changed = expected.clone();
            changed[index].size += 1;
            assert!(plan(TAG, &expected, Some(&release(changed))).is_err());
            let mut changed = expected.clone();
            changed[index].digest[0] ^= 1;
            assert!(plan(TAG, &expected, Some(&release(changed))).is_err());
            let mut duplicate = expected.clone();
            duplicate.push(expected[index].clone());
            assert!(plan(TAG, &expected, Some(&release(duplicate))).is_err());
        }
        let mut unknown = expected.clone();
        unknown[0].name = "unexpected.txt".into();
        assert!(plan(TAG, &expected, Some(&release(unknown))).is_err());
    }

    #[test]
    fn asset_decisions_cover_all_input_combinations() {
        for present in [false, true] {
            for uploaded in [false, true] {
                for same_size in [false, true] {
                    for same_digest in [false, true] {
                        let decision = asset_decision(present, uploaded, same_size, same_digest);
                        assert_eq!(decision == AssetDecision::Upload, !present);
                        assert_eq!(
                            decision == AssetDecision::Reuse,
                            present && uploaded && same_size && same_digest
                        );
                        assert_eq!(
                            decision == AssetDecision::Reject,
                            present && !(uploaded && same_size && same_digest)
                        );
                    }
                }
            }
        }
    }

    #[test]
    fn publication_requires_verified_complete_draft() {
        for draft in [false, true] {
            for identity in [false, true] {
                for missing in [0, 1, 8, usize::MAX] {
                    assert_eq!(
                        can_publish(draft, identity, missing),
                        draft && identity && missing == 0
                    );
                }
            }
        }
    }
}
