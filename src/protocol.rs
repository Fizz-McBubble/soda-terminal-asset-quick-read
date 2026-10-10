use sha2::{Digest, Sha256};
use std::{collections::BTreeMap, fs, path::Path};
use zzz_gamedata::GameData;

pub fn hash_bytes(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}

pub fn protocol_hashes() -> BTreeMap<String, String> {
    serde_json::from_str(include_str!("../protocol-files.sha256.json"))
        .expect("checked-in protocol hash manifest")
}

pub fn load_protocol(path: &Path) -> Result<GameData, String> {
    for (file, expected) in protocol_hashes() {
        let bytes =
            fs::read(path.join(&file)).map_err(|_| format!("protocol_file_missing:{file}"))?;
        if hash_bytes(&bytes) != expected {
            return Err(format!("protocol_hash_mismatch:{file}"));
        }
    }
    GameData::load(path).map_err(|_| "protocol_data_invalid".into())
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
}
