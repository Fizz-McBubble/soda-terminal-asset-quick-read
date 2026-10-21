//! Read only the two version-bound request fields from an explicitly supplied
//! static client resource. No game process, credential field, or network access.

use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{fs, io::Read, path::Path};

const MAX_FILE_BYTES: u64 = 64 * 1024 * 1024;
const MAX_OBJECT_BYTES: usize = 16 * 1024;
const EXPECTED_VERSION: &str = "CNPRODWin3.2.0";
const VERSION_MARKER: &[u8] = b"\"DispatchVersion\"";
const SEED_MARKER: &[u8] = b"\"DispatchSeed\"";

#[derive(Debug, Clone, Default, Serialize)]
pub struct ClientDispatchReceipt {
    pub status: &'static str,
    pub source_bytes_read: u64,
    pub source_sha256: Option<String>,
    pub dispatch_version_field_occurrences: usize,
    pub dispatch_seed_field_occurrences: usize,
    pub same_json_object: bool,
    pub expected_cn_version_matches: bool,
    pub seed_characters: Option<usize>,
    pub seed_is_16_hex: bool,
}

// Never implement Debug, Display, or Serialize for objects containing seed values.
pub struct Inspection {
    pub receipt: ClientDispatchReceipt,
    seed: Option<String>,
}

impl Inspection {
    pub fn into_receipt(self) -> ClientDispatchReceipt {
        self.receipt
    }

    pub(crate) fn into_seed_and_receipt(self) -> (Option<String>, ClientDispatchReceipt) {
        (self.seed, self.receipt)
    }
}

#[derive(Deserialize)]
struct RequestFields {
    #[serde(rename = "DispatchVersion")]
    version: String,
    #[serde(rename = "DispatchSeed")]
    seed: String,
    // serde skips all other values without retaining them in this object.
}

pub fn inspect(input: &Path) -> Inspection {
    let mut receipt = ClientDispatchReceipt {
        status: "client_resource_input_rejected",
        ..Default::default()
    };
    if input.file_name().and_then(|name| name.to_str()) != Some("resources.assets") {
        return Inspection {
            receipt,
            seed: None,
        };
    }
    let Ok(metadata) = fs::symlink_metadata(input) else {
        receipt.status = "client_resource_unavailable";
        return Inspection {
            receipt,
            seed: None,
        };
    };
    if !metadata.file_type().is_file() || metadata.len() > MAX_FILE_BYTES {
        receipt.status = "client_resource_type_or_size_rejected";
        return Inspection {
            receipt,
            seed: None,
        };
    }
    let Ok(file) = fs::File::open(input) else {
        receipt.status = "client_resource_unavailable";
        return Inspection {
            receipt,
            seed: None,
        };
    };
    let mut bytes = Vec::new();
    if file
        .take(MAX_FILE_BYTES + 1)
        .read_to_end(&mut bytes)
        .is_err()
    {
        receipt.status = "client_resource_read_failed";
        return Inspection {
            receipt,
            seed: None,
        };
    }
    receipt.source_bytes_read = bytes.len() as u64;
    if bytes.len() as u64 > MAX_FILE_BYTES {
        receipt.status = "client_resource_type_or_size_rejected";
        return Inspection {
            receipt,
            seed: None,
        };
    }
    inspect_bytes(&bytes)
}

fn field_positions(bytes: &[u8], marker: &[u8]) -> Vec<usize> {
    bytes
        .windows(marker.len())
        .enumerate()
        .filter_map(|(index, window)| {
            if window != marker {
                return None;
            }
            let suffix = &bytes[index + marker.len()..];
            suffix
                .iter()
                .find(|byte| !byte.is_ascii_whitespace())
                .filter(|byte| **byte == b':')
                .map(|_| index)
        })
        .take(65)
        .collect()
}

fn object_end(bytes: &[u8], start: usize) -> Option<usize> {
    let mut depth = 0_u32;
    let mut quoted = false;
    let mut escaped = false;
    for (index, &byte) in bytes.iter().enumerate().skip(start).take(MAX_OBJECT_BYTES) {
        if quoted {
            if escaped {
                escaped = false;
            } else if byte == b'\\' {
                escaped = true;
            } else if byte == b'"' {
                quoted = false;
            }
            continue;
        }
        match byte {
            b'"' => quoted = true,
            b'{' => {
                depth += 1;
                if depth > 64 {
                    return None;
                }
            }
            b'}' => {
                depth = depth.checked_sub(1)?;
                if depth == 0 {
                    return Some(index + 1);
                }
            }
            _ => {}
        }
    }
    None
}

fn inspect_bytes(bytes: &[u8]) -> Inspection {
    let versions = field_positions(bytes, VERSION_MARKER);
    let seeds = field_positions(bytes, SEED_MARKER);
    let mut receipt = ClientDispatchReceipt {
        status: "client_dispatch_fields_missing_or_ambiguous",
        source_bytes_read: bytes.len() as u64,
        source_sha256: Some(format!("{:X}", Sha256::digest(bytes))),
        dispatch_version_field_occurrences: versions.len(),
        dispatch_seed_field_occurrences: seeds.len(),
        ..Default::default()
    };
    if versions.len() != 1 || seeds.len() != 1 {
        return Inspection {
            receipt,
            seed: None,
        };
    }
    let first = versions[0].min(seeds[0]);
    let last = versions[0].max(seeds[0]);
    let lower = first.saturating_sub(MAX_OBJECT_BYTES);
    for start in (lower..first)
        .rev()
        .filter(|&index| bytes[index] == b'{')
        .take(64)
    {
        let Some(end) = object_end(bytes, start).filter(|&end| end > last) else {
            continue;
        };
        // Deserialize only the two selected fields. Unknown fields are ignored;
        // duplicate, malformed, and cross-object pairs cannot become a request.
        let Ok(fields) = serde_json::from_slice::<RequestFields>(&bytes[start..end]) else {
            continue;
        };
        receipt.same_json_object = true;
        receipt.expected_cn_version_matches = fields.version == EXPECTED_VERSION;
        receipt.seed_characters = Some(fields.seed.chars().count());
        receipt.seed_is_16_hex =
            fields.seed.len() == 16 && fields.seed.bytes().all(|byte| byte.is_ascii_hexdigit());
        if !receipt.expected_cn_version_matches || !receipt.seed_is_16_hex {
            receipt.status = match (receipt.expected_cn_version_matches, receipt.seed_is_16_hex) {
                (false, true) => "client_dispatch_version_rejected",
                (true, false) => "client_dispatch_seed_shape_rejected",
                _ => "client_dispatch_version_and_seed_shape_rejected",
            };
            return Inspection {
                receipt,
                seed: None,
            };
        }
        receipt.status = "client_request_fields_valid";
        return Inspection {
            receipt,
            seed: Some(fields.seed),
        };
    }
    receipt.status = "client_dispatch_same_object_unverified";
    Inspection {
        receipt,
        seed: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(version: &str, seed: &str) -> Vec<u8> {
        format!("{{\"DispatchVersion\":\"{version}\",\"DispatchSeed\":\"{seed}\",\"Ignored\":{{\"value\":\"private-example-do-not-export\"}}}}").into_bytes()
    }

    #[test]
    fn binary_wrapping_and_reversed_field_order_do_not_export_values() {
        let mut bytes = vec![0xff, 0, 0x80];
        bytes.extend_from_slice(br#"{"DispatchSeed":"0123456789ABCDEF","DispatchVersion":"CNPRODWin3.2.0","Ignored":"private-example-do-not-export"}"#);
        bytes.extend_from_slice(&[0, 0xff]);
        let inspected = inspect_bytes(&bytes);
        assert_eq!(inspected.receipt.status, "client_request_fields_valid");
        assert_eq!(inspected.seed.as_deref(), Some("0123456789ABCDEF"));
        let safe = serde_json::to_string(&inspected.into_receipt()).unwrap();
        assert!(!safe.contains("0123456789ABCDEF"));
        assert!(!safe.contains("private-example"));
    }

    #[test]
    fn missing_duplicate_and_cross_object_pairs_are_not_request_inputs() {
        for bytes in [
            br#"{"DispatchVersion":"CNPRODWin3.2.0"}"#.to_vec(),
            br#"{"DispatchVersion":"CNPRODWin3.2.0","DispatchSeed":"0123456789abcdef","DispatchSeed":"0123456789abcdef"}"#.to_vec(),
            br#"{"DispatchVersion":"CNPRODWin3.2.0"}{"DispatchSeed":"0123456789abcdef"}"#.to_vec(),
        ] {
            let inspected = inspect_bytes(&bytes);
            assert!(inspected.seed.is_none());
            assert!(!inspected.receipt.same_json_object);
        }
    }

    #[test]
    fn wrong_version_and_unbounded_or_nonhex_seed_are_rejected() {
        for (version, seed, expected) in [
            (
                "OSPRODWin3.2.0",
                "0123456789abcdef",
                "client_dispatch_version_rejected",
            ),
            (
                "CNPRODWin3.1.0",
                "0123456789abcdef",
                "client_dispatch_version_rejected",
            ),
            (
                EXPECTED_VERSION,
                "0123456789abcdeg",
                "client_dispatch_seed_shape_rejected",
            ),
            (
                EXPECTED_VERSION,
                "0123",
                "client_dispatch_seed_shape_rejected",
            ),
            (
                "OSPRODWin3.2.0",
                "0123",
                "client_dispatch_version_and_seed_shape_rejected",
            ),
        ] {
            let inspected = inspect_bytes(&fixture(version, seed));
            assert!(inspected.seed.is_none());
            assert_eq!(inspected.receipt.status, expected);
        }
    }

    #[test]
    fn malformed_oversized_and_nested_pairs_do_not_pass() {
        let nested = br#"{"a":{"DispatchVersion":"CNPRODWin3.2.0"},"b":{"DispatchSeed":"0123456789abcdef"}}"#;
        assert!(inspect_bytes(nested).seed.is_none());
        assert!(
            inspect_bytes(br#"{"DispatchVersion":"CNPRODWin3.2.0","DispatchSeed":16}"#)
                .seed
                .is_none()
        );
        let mut oversized = fixture(EXPECTED_VERSION, "0123456789abcdef");
        oversized.pop();
        oversized.extend(std::iter::repeat_n(b' ', MAX_OBJECT_BYTES));
        oversized.push(b'}');
        assert!(inspect_bytes(&oversized).seed.is_none());
    }
}
