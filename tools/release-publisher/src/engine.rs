use crate::core::{self, Asset, Plan, Release};

pub trait Port {
    fn local_assets(&mut self) -> Result<Vec<Asset>, String>;
    fn verify_identity(&mut self, repo: &str, tag: &str, sha: &str) -> Result<(), String>;
    fn current_release(&mut self, tag: &str) -> Result<Option<Release>, String>;
    fn create_draft(&mut self, tag: &str, sha: &str) -> Result<u64, String>;
    fn upload(&mut self, release_id: u64, name: &str) -> Result<(), String>;
    fn publish(&mut self, release_id: u64) -> Result<(), String>;
    fn verify_published(
        &mut self,
        release_id: u64,
        tag: &str,
        expected: &[Asset],
    ) -> Result<(), String>;
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Outcome {
    pub plan: Plan,
    pub published: bool,
}

pub fn run(
    port: &mut impl Port,
    repo: &str,
    tag: &str,
    sha: &str,
    apply: bool,
) -> Result<Outcome, String> {
    core::validate_identity(repo, tag, sha)
        .map_err(|error| format!("release identity validation failed: {error}"))?;

    let expected = port
        .local_assets()
        .map_err(|error| format!("local payload inspection failed: {error}"))?;
    port.verify_identity(repo, tag, sha)
        .map_err(|error| format!("release identity verification failed: {error}"))?;
    let current = port
        .current_release(tag)
        .map_err(|error| format!("release lookup failed: {error}"))?;
    let initial_plan = core::plan(tag, &expected, current.as_ref())
        .map_err(|error| format!("release planning failed: {error}"))?;

    if !apply {
        return Ok(Outcome {
            plan: initial_plan,
            published: false,
        });
    }

    let release_id = match current {
        Some(release) => release.id,
        None => {
            port.verify_identity(repo, tag, sha).map_err(|error| {
                format!("release identity revalidation failed before draft creation: {error}")
            })?;
            port.create_draft(tag, sha).map_err(|error| {
                format!("draft creation failed; remote draft state may be uncertain: {error}")
            })?
        }
    };

    let draft = port.current_release(tag).map_err(|error| {
        format!(
            "draft readback failed after creation or lookup; remote state may be uncertain: {error}"
        )
    })?;
    let draft = draft.ok_or_else(|| {
        "draft readback failed: expected release is absent after creation or lookup; remote state may be uncertain".to_owned()
    })?;
    if draft.id != release_id || draft.tag != tag || !draft.draft {
        return Err(format!(
            "draft readback failed: expected release id {release_id} in draft for tag {tag}, found id {} tag {} draft={}",
            draft.id, draft.tag, draft.draft
        ));
    }

    let draft_plan = core::plan(tag, &expected, Some(&draft))
        .map_err(|error| format!("draft asset planning failed: {error}"))?;
    if draft_plan.release_id != Some(release_id) {
        return Err("draft asset planning failed: release identity changed".to_owned());
    }
    if !draft_plan.missing.is_empty() {
        let before_upload = port
            .local_assets()
            .map_err(|error| format!("local payload recheck failed before upload: {error}"))?;
        if before_upload != expected {
            return Err(
                "local payload recheck failed: asset bytes changed before upload".to_owned(),
            );
        }
        port.verify_identity(repo, tag, sha).map_err(|error| {
            format!(
                "release identity revalidation failed before upload; draft may already exist and no asset upload was attempted: {error}"
            )
        })?;
    }

    for name in &draft_plan.missing {
        port.upload(release_id, name).map_err(|error| {
            format!("upload failed for {name}; remote asset mutation may have occurred: {error}")
        })?;
    }

    let after_upload = port
        .local_assets()
        .map_err(|error| format!("local payload readback failed after upload: {error}"))?;
    if after_upload != expected {
        return Err(
            "local payload readback failed after upload: asset bytes changed during publication"
                .to_owned(),
        );
    }

    port.verify_identity(repo, tag, sha).map_err(|error| {
        format!("release identity revalidation failed after upload; publication was not attempted: {error}")
    })?;
    let complete = port.current_release(tag).map_err(|error| {
        format!("draft asset readback failed; remote mutation may have occurred: {error}")
    })?;
    let complete = complete.ok_or_else(|| {
        "draft asset readback failed: release is absent; remote mutation may have occurred"
            .to_owned()
    })?;
    if complete.id != release_id {
        return Err(format!(
            "draft asset readback failed: expected release id {release_id}, found {}",
            complete.id
        ));
    }
    let complete_plan = core::plan(tag, &expected, Some(&complete))
        .map_err(|error| format!("draft asset readback failed: {error}"))?;
    if !core::can_publish(complete.draft, true, complete_plan.missing.len()) {
        return Err(format!(
            "publication refused: draft={}, identity_verified=true, missing_assets={}",
            complete.draft,
            complete_plan.missing.len()
        ));
    }

    port.publish(release_id).map_err(|error| {
        format!("publish request failed; release may already be published: {error}")
    })?;
    port.verify_published(release_id, tag, &expected)
        .map_err(|error| {
            format!("published release readback failed; release may already be published: {error}")
        })?;
    port.verify_identity(repo, tag, sha).map_err(|error| {
        format!(
            "post-publish identity verification failed; release may already be published: {error}"
        )
    })?;

    Ok(Outcome {
        plan: complete_plan,
        published: true,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const REPO: &str = "P4suta/Snaply";
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

    #[derive(Clone, Debug, Eq, PartialEq)]
    enum Event {
        LocalAssets,
        VerifyIdentity(String, String),
        CurrentRelease(String),
        CreateDraft(String, String),
        Upload(u64, String),
        Publish(u64),
        VerifyPublished(u64, String),
    }

    struct FakePort {
        assets: Vec<Asset>,
        current: Option<Release>,
        events: Vec<Event>,
        upload_error: Option<String>,
        published_error: Option<String>,
        local_reads: usize,
        change_local_on_read: Option<usize>,
        change_release_on_read: Option<(usize, u64)>,
        fail_identity_on_call: Option<usize>,
        create_id: u64,
        mutate_uploads: bool,
    }

    impl FakePort {
        fn new() -> Self {
            Self {
                assets: assets(),
                current: None,
                events: Vec::new(),
                upload_error: None,
                published_error: None,
                local_reads: 0,
                change_local_on_read: None,
                change_release_on_read: None,
                fail_identity_on_call: None,
                create_id: 41,
                mutate_uploads: true,
            }
        }

        fn with_release(mut self, release: Release) -> Self {
            self.current = Some(release);
            self
        }

        fn writes(&self) -> Vec<&Event> {
            self.events
                .iter()
                .filter(|event| {
                    matches!(
                        event,
                        Event::CreateDraft(_, _) | Event::Upload(_, _) | Event::Publish(_)
                    )
                })
                .collect()
        }
    }

    impl Port for FakePort {
        fn local_assets(&mut self) -> Result<Vec<Asset>, String> {
            self.events.push(Event::LocalAssets);
            self.local_reads += 1;
            let mut assets = self.assets.clone();
            if self.change_local_on_read == Some(self.local_reads) {
                assets[0].digest[0] ^= 1;
            }
            Ok(assets)
        }

        fn verify_identity(&mut self, repo: &str, tag: &str, _sha: &str) -> Result<(), String> {
            self.events
                .push(Event::VerifyIdentity(repo.to_owned(), tag.to_owned()));
            let calls = self
                .events
                .iter()
                .filter(|event| matches!(event, Event::VerifyIdentity(_, _)))
                .count();
            if self.fail_identity_on_call == Some(calls) {
                return Err("remote tag no longer identifies the selected commit".to_owned());
            }
            Ok(())
        }

        fn current_release(&mut self, tag: &str) -> Result<Option<Release>, String> {
            self.events.push(Event::CurrentRelease(tag.to_owned()));
            let reads = self
                .events
                .iter()
                .filter(|event| matches!(event, Event::CurrentRelease(_)))
                .count();
            if let Some((change_on_read, id)) = self.change_release_on_read
                && reads == change_on_read
                && let Some(release) = self.current.as_mut()
            {
                release.id = id;
            }
            Ok(self.current.clone())
        }

        fn create_draft(&mut self, tag: &str, sha: &str) -> Result<u64, String> {
            self.events
                .push(Event::CreateDraft(tag.to_owned(), sha.to_owned()));
            self.current = Some(Release {
                id: self.create_id,
                tag: tag.to_owned(),
                draft: true,
                assets: Vec::new(),
            });
            Ok(self.create_id)
        }

        fn upload(&mut self, release_id: u64, name: &str) -> Result<(), String> {
            self.events.push(Event::Upload(release_id, name.to_owned()));
            if let Some(error) = self.upload_error.take() {
                return Err(error);
            }
            if self.mutate_uploads {
                let asset = self
                    .assets
                    .iter()
                    .find(|asset| asset.name == name)
                    .expect("test upload uses an expected asset")
                    .clone();
                let release = self.current.as_mut().expect("test release exists");
                assert_eq!(release.id, release_id);
                release.assets.push(asset);
            }
            Ok(())
        }

        fn publish(&mut self, release_id: u64) -> Result<(), String> {
            self.events.push(Event::Publish(release_id));
            if let Some(release) = self.current.as_mut() {
                assert_eq!(release.id, release_id);
                release.draft = false;
            }
            Ok(())
        }

        fn verify_published(
            &mut self,
            release_id: u64,
            tag: &str,
            _expected: &[Asset],
        ) -> Result<(), String> {
            self.events
                .push(Event::VerifyPublished(release_id, tag.to_owned()));
            if let Some(error) = self.published_error.take() {
                return Err(error);
            }
            Ok(())
        }
    }

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
    fn planning_performs_no_mutations_and_returns_the_core_plan() {
        let current = release(assets()[..3].to_vec());
        let mut port = FakePort::new().with_release(current.clone());

        let outcome = run(&mut port, REPO, TAG, SHA, false).unwrap();

        assert_eq!(
            outcome.plan,
            core::plan(TAG, &assets(), Some(&current)).unwrap()
        );
        assert!(!outcome.published);
        assert!(port.writes().is_empty());
    }

    #[test]
    fn apply_creates_a_draft_uploads_all_assets_then_publishes_and_verifies() {
        let mut port = FakePort::new();

        let outcome = run(&mut port, REPO, TAG, SHA, true).unwrap();

        assert_eq!(outcome.plan.release_id, Some(41));
        assert!(outcome.plan.missing.is_empty());
        assert!(outcome.published);
        assert!(!port.current.as_ref().unwrap().draft);
        assert_eq!(
            port.writes(),
            vec![
                &Event::CreateDraft(TAG.into(), SHA.into()),
                &Event::Upload(41, NAMES[0].into()),
                &Event::Upload(41, NAMES[1].into()),
                &Event::Upload(41, NAMES[2].into()),
                &Event::Upload(41, NAMES[3].into()),
                &Event::Upload(41, NAMES[4].into()),
                &Event::Upload(41, NAMES[5].into()),
                &Event::Upload(41, NAMES[6].into()),
                &Event::Upload(41, NAMES[7].into()),
                &Event::Publish(41),
            ]
        );
        assert!(matches!(
            port.events.last(),
            Some(Event::VerifyIdentity(_, _))
        ));
    }

    #[test]
    fn apply_reuses_only_missing_assets_on_a_matching_partial_draft() {
        let mut port = FakePort::new().with_release(release(assets()[..2].to_vec()));

        let outcome = run(&mut port, REPO, TAG, SHA, true).unwrap();

        assert_eq!(outcome.plan.release_id, Some(42));
        assert!(outcome.plan.missing.is_empty());
        let uploaded: Vec<_> = port
            .events
            .iter()
            .filter_map(|event| match event {
                Event::Upload(42, name) => Some(name.as_str()),
                _ => None,
            })
            .collect();
        assert_eq!(uploaded, NAMES[2..]);
        assert!(outcome.published);
    }

    #[test]
    fn upload_failure_stops_without_publish_and_reports_possible_partial_mutation() {
        let mut port = FakePort::new();
        port.upload_error = Some("network disconnected".into());

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("upload"));
        assert!(error.contains("mutation"));
        assert!(
            !port
                .events
                .iter()
                .any(|event| matches!(event, Event::Publish(_)))
        );
        assert_eq!(
            port.events
                .iter()
                .filter(|event| matches!(event, Event::Upload(_, _)))
                .count(),
            1
        );
    }

    #[test]
    fn refuses_a_release_id_change_after_draft_creation() {
        let mut port = FakePort::new();
        port.change_release_on_read = Some((2, 99));

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("release"));
        assert!(
            port.writes()
                .iter()
                .all(|event| !matches!(event, Event::Upload(_, _) | Event::Publish(_)))
        );
    }

    #[test]
    fn refuses_local_asset_bytes_that_change_before_upload() {
        let mut port = FakePort::new();
        port.change_local_on_read = Some(2);

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("local"));
        assert!(
            !port
                .events
                .iter()
                .any(|event| matches!(event, Event::Upload(_, _) | Event::Publish(_)))
        );
    }

    #[test]
    fn tag_move_after_draft_readback_prevents_the_first_upload() {
        let mut port = FakePort::new();
        port.fail_identity_on_call = Some(3);

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("identity revalidation"));
        assert!(error.contains("no asset upload was attempted"));
        let create_index = port
            .events
            .iter()
            .position(|event| matches!(event, Event::CreateDraft(_, _)))
            .unwrap();
        let draft_readback_index = port
            .events
            .iter()
            .enumerate()
            .filter(|(_, event)| matches!(event, Event::CurrentRelease(_)))
            .nth(1)
            .map(|(index, _)| index)
            .unwrap();
        let failed_identity_index = port
            .events
            .iter()
            .enumerate()
            .filter(|(_, event)| matches!(event, Event::VerifyIdentity(_, _)))
            .nth(2)
            .map(|(index, _)| index)
            .unwrap();

        assert!(create_index < draft_readback_index);
        assert!(draft_readback_index < failed_identity_index);
        assert!(
            !port
                .events
                .iter()
                .any(|event| matches!(event, Event::Upload(_, _) | Event::Publish(_)))
        );
    }

    #[test]
    fn upload_readback_mismatch_prevents_publish() {
        let mut port = FakePort::new();
        port.mutate_uploads = false;

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("asset") || error.contains("upload"));
        assert!(
            !port
                .events
                .iter()
                .any(|event| matches!(event, Event::Publish(_)))
        );
    }

    #[test]
    fn refuses_a_release_id_change_after_upload_readback() {
        let mut port = FakePort::new();
        port.change_release_on_read = Some((3, 99));

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("release"));
        assert!(
            !port
                .events
                .iter()
                .any(|event| matches!(event, Event::Publish(_)))
        );
    }

    #[test]
    fn refuses_published_release_without_mutation() {
        let mut published = release(assets());
        published.draft = false;
        let mut port = FakePort::new().with_release(published);

        assert!(run(&mut port, REPO, TAG, SHA, true).is_err());
        assert!(port.writes().is_empty());
    }

    #[test]
    fn publish_readback_failure_is_reported_as_uncertain_not_success() {
        let mut port = FakePort::new();
        port.published_error = Some("release lookup unavailable".into());

        let error = run(&mut port, REPO, TAG, SHA, true).unwrap_err();

        assert!(error.contains("publish") || error.contains("readback"));
        assert!(!error.to_lowercase().contains("success"));
    }
}
