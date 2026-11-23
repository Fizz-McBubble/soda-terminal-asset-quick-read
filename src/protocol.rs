use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, fs, path::Path, sync::OnceLock};
use zzz_gamedata::{Datamine, GameData, NanokaData};
use zzz_wire::{ProtoEntry, Protonap};

pub const PARSER_UPSTREAM_COMMIT: &str = "4853f07c8fb5322c12b9b580d6364b577c2ac96a";
pub const PROTOCOL_UPSTREAM_COMMIT: &str = "444d2ccf3bae448d6a205e5d35107e85d462875a";

pub fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

pub fn protocol_hashes() -> BTreeMap<String, String> {
    serde_json::from_str(include_str!("../protocol-files.sha256.json"))
        .expect("checked-in protocol hash manifest")
}

/// The checked, compiled protocol bytes own the version. A website data-pack
/// update cannot relabel this parser or open a newer client contract.
pub fn pinned_protocol_version() -> &'static str {
    static VERSION: OnceLock<String> = OnceLock::new();
    VERSION.get_or_init(|| {
        let bytes = include_bytes!("../.upstream/assets/manifest.json");
        let hashes = protocol_hashes();
        manifest_version(bytes, &hashes["manifest.json"])
            .expect("compiled protocol manifest must match its reviewed SHA and version shape")
    })
}

pub fn pinned_cn_client_version() -> String {
    format!("CNPRODWin{}.0", pinned_protocol_version())
}

fn manifest_version(bytes: &[u8], expected_hash: &str) -> Result<String, &'static str> {
    if hash_bytes(bytes) != expected_hash {
        return Err("protocol_manifest_hash_mismatch");
    }
    let manifest: serde_json::Value =
        serde_json::from_slice(bytes).map_err(|_| "protocol_manifest_invalid")?;
    let version = manifest["version"]
        .as_str()
        .ok_or("protocol_manifest_version_missing")?;
    let components: Vec<_> = version.split('.').collect();
    if components.len() != 2
        || components.iter().any(|part| {
            part.is_empty()
                || part.len() > 3
                || !part.bytes().all(|byte| byte.is_ascii_digit())
                || (part.len() > 1 && part.starts_with('0'))
        })
    {
        return Err("protocol_manifest_version_invalid");
    }
    Ok(version.to_owned())
}

pub fn load_protocol(path: &Path) -> Result<GameData, String> {
    let mut bytes = BTreeMap::new();
    for (file, expected) in protocol_hashes() {
        let contents =
            fs::read(path.join(&file)).map_err(|_| format!("protocol_file_missing:{file}"))?;
        if hash_bytes(&contents) != expected {
            return Err(format!("protocol_hash_mismatch:{file}"));
        }
        bytes.insert(file, contents);
    }
    let manifest = serde_json::from_slice::<serde_json::Value>(&bytes["manifest.json"])
        .map_err(|_| "protocol_data_invalid")?;
    let version = manifest["version"]
        .as_str()
        .filter(|version| *version == pinned_protocol_version())
        .ok_or("protocol_data_invalid")?;
    let datamine = parse_datamine_compat(&bytes["datamine.json"], version)?;
    let proto_entries = serde_json::from_slice::<Vec<ProtoEntry>>(&bytes["nap.json"])
        .map_err(|_| "protocol_data_invalid")?;
    Ok(GameData {
        datamine,
        nap: Protonap::new(proto_entries.clone()),
        proto_entries,
        // Names are presentation-only and deliberately absent from the runtime package.
        nanoka: NanokaData::default(),
    })
}

/// The Rust parser is a pinned 3.2 implementation. Version 3.3 removed five
/// incremental/update fields that this response-only reader never consumes.
/// Supply fixed non-command sentinels so the reviewed 3.3 load-response schema
/// can be decoded without inventing values or accepting an arbitrary datamine.
fn parse_datamine_compat(bytes: &[u8], version: &str) -> Result<Datamine, String> {
    let mut value =
        serde_json::from_slice::<serde_json::Value>(bytes).map_err(|_| "protocol_data_invalid")?;
    if version == "3.3" {
        let object = value.as_object_mut().ok_or("protocol_data_invalid")?;
        object
            .entry("cmdPlayerSyncScNotify")
            .or_insert_with(|| serde_json::json!(u32::MAX));
        object
            .entry("cmdDismantleEquipCsReq")
            .or_insert_with(|| serde_json::json!(u32::MAX));
        object
            .entry("syncAvatarData")
            .or_insert_with(|| serde_json::json!({"avatarSync": 0, "avatars": 0, "dels": 0}));
        object.entry("syncItemData").or_insert_with(|| {
            serde_json::json!({
                "itemSync": 0,
                "equips": 0,
                "weapons": 0,
                "deletedEquips": 0
            })
        });
        object
            .entry("equipDismantle")
            .or_insert_with(|| serde_json::json!({"uid": 0, "uids": 0}));
    }
    serde_json::from_value(value).map_err(|_| "protocol_data_invalid".into())
}

pub fn verify_driver_files(directory: &Path) -> Result<(), String> {
    let hashes: BTreeMap<String, String> =
        serde_json::from_str(include_str!("../driver-files.sha256.json"))
            .expect("checked-in driver hash manifest");
    for (file, expected) in hashes {
        let bytes =
            fs::read(directory.join(&file)).map_err(|_| format!("driver_file_missing:{file}"))?;
        if hash_bytes(&bytes) != expected {
            return Err(format!("driver_hash_mismatch:{file}"));
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn pin_covers_protocol_and_driver_separately() {
        let protocol = protocol_hashes();
        assert_eq!(protocol.len(), 3);
        assert!(protocol.contains_key("nap.json"));
        assert_eq!(
            hash_bytes(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
    }

    #[test]
    fn client_version_follows_the_actual_reviewed_protocol_manifest() {
        assert_eq!(pinned_protocol_version(), "3.3");
        assert_eq!(pinned_cn_client_version(), "CNPRODWin3.3.0");
    }

    #[test]
    fn relabelled_or_malformed_protocol_cannot_inherit_the_reviewed_hash() {
        let fabricated = br#"{"version":"3.3"}"#;
        assert_eq!(
            manifest_version(fabricated, &protocol_hashes()["manifest.json"]),
            Err("protocol_manifest_hash_mismatch")
        );
        for invalid in ["3", "3.3.0", "03.3", "3.3?x=1", "3.3\n"] {
            let bytes = serde_json::to_vec(&serde_json::json!({ "version": invalid })).unwrap();
            assert_eq!(
                manifest_version(&bytes, &hash_bytes(&bytes)),
                Err("protocol_manifest_version_invalid")
            );
        }
    }

    #[test]
    fn a_distinct_reviewed_manifest_can_carry_a_new_version_without_string_substitution() {
        let synthetic = br#"{"version":"3.4"}"#;
        assert_eq!(
            manifest_version(synthetic, &hash_bytes(synthetic)),
            Ok("3.4".into())
        );
        // This parsing unit does not supply real future nap/datamine mappings.
        assert_eq!(pinned_protocol_version(), "3.3");
    }

    #[test]
    fn reviewed_33_data_loads_actual_response_mappings_and_legacy_sentinels() {
        let data = load_protocol(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".upstream/assets"),
        )
        .unwrap();
        assert_eq!(data.datamine.cmd_player_get_token_sc_rsp, 2452);
        assert_eq!(data.datamine.cmd_get_equip_data_sc_rsp, 1744);
        assert_eq!(data.datamine.cmd_get_weapon_data_sc_rsp, 8256);
        assert_eq!(data.datamine.cmd_get_avatar_data_sc_rsp, 7398);
        assert_eq!(data.datamine.cmd_player_sync_sc_notify, u32::MAX);
        assert_eq!(data.datamine.cmd_dismantle_equip_cs_req, u32::MAX);
        assert_eq!(data.proto_entries.len(), data.nap.entries().len());
    }
}
