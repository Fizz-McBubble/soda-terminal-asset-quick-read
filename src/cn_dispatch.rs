//! Opt-in, anonymous public configuration bootstrap. No account or login APIs.
//! Network bodies and derived initial configuration remain in memory only.

use serde::Serialize;
use serde_json::Value;
use std::path::Path;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use ureq::{http::Uri, Agent};
use zzz_crypto::{b64_decode, ec2b, rsa};

pub const QUERY_URI: &str = "https://globaldp-prod-cn01.juequling.com/query_dispatch?version=CNPRODWin3.2.0&language=2&channel_id=1&sub_channel_id=1&platform=3";
pub const LABEL: &str = "CN-public-dispatch-candidate";
const BODY_LIMIT: u64 = 65536;

#[derive(Debug, Clone, Default, Serialize)]
pub struct MetadataReceipt {
    pub request_seed_source: Option<&'static str>,
    pub client_resource_fields: Option<crate::client_dispatch::ClientDispatchReceipt>,
    pub requests_attempted: u32,
    pub query_http_status: Option<u16>,
    pub query_return_code: Option<i64>,
    pub gateway_http_status: Option<u16>,
    pub gateway_return_code: Option<i64>,
    pub region_entries: usize,
    pub gateway_url_allowed: bool,
    pub rsa_metadata_decoded: bool,
    pub metadata_json_decoded: bool,
    pub metadata_return_code: Option<i64>,
    pub metadata_return_code_invalid_type: bool,
    pub metadata_message_characters: usize,
    pub metadata_version_hint_count: u8,
    pub metadata_parameter_hint_count: u8,
    pub metadata_nonce_hint_count: u8,
    pub metadata_signature_hint_count: u8,
    pub metadata_maintenance_hint_count: u8,
    pub ec2b_metadata_decoded: bool,
    pub signature_verified: bool,
    pub response_payload_saved: bool,
    pub credentials_used: bool,
}

// Deliberately neither Debug nor Serialize: no seed escapes via generic logging.
pub struct Bootstrap {
    initial_seed: u64,
    pub receipt: MetadataReceipt,
}

impl Bootstrap {
    /// Consume the in-memory configuration without starting a packet pipeline.
    pub fn into_receipt(self) -> MetadataReceipt {
        self.receipt
    }

    pub fn into_pipeline<'a>(
        self,
        data: &'a zzz_gamedata::GameData,
    ) -> (crate::pipeline::Pipeline<'a>, MetadataReceipt) {
        (
            crate::pipeline::Pipeline::new(LABEL.into(), self.initial_seed, data),
            self.receipt,
        )
    }
}

pub struct Failure {
    pub code: &'static str,
    pub receipt: Box<MetadataReceipt>,
}

fn network_agent() -> Agent {
    Agent::config_builder()
        .timeout_global(Some(Duration::from_secs(20)))
        .proxy(None)
        .max_idle_connections(0)
        .max_idle_connections_per_host(0)
        .max_redirects(0)
        .http_status_as_error(false)
        .build()
        .into()
}

/// Requires explicit approval for at most these two public HTTPS GETs.
/// This is never called by inspect, replay, or the default capture command.
pub fn fetch_approved_configuration() -> Result<Bootstrap, Failure> {
    let nonce = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos() as u64;
    fetch_actual(&format!("{nonce:016x}"), None)
}

/// Separately approved local static fields plus the same two anonymous GETs.
/// Invalid fields stop before the first GET; no field values are exported.
pub fn fetch_client_configuration(input: &Path) -> Result<Bootstrap, Failure> {
    let (seed, fields) = crate::client_dispatch::inspect(input).into_seed_and_receipt();
    let Some(seed) = seed else {
        return Err(Failure {
            code: fields.status,
            receipt: Box::new(MetadataReceipt {
                request_seed_source: Some("local_client_resources"),
                client_resource_fields: Some(fields),
                ..Default::default()
            }),
        });
    };
    fetch_actual(&seed, Some(fields))
}

fn fetch_actual(
    seed: &str,
    fields: Option<crate::client_dispatch::ClientDispatchReceipt>,
) -> Result<Bootstrap, Failure> {
    let agent = network_agent();
    fetch_seed_with(seed, fields, |uri| {
        let mut response = agent
            .get(uri)
            .call()
            .map_err(|_| "public_configuration_request_failed")?;
        let status = response.status().as_u16();
        if status != 200 {
            return Ok((status, String::new()));
        }
        let body = response
            .body_mut()
            .with_config()
            .limit(BODY_LIMIT)
            .read_to_string()
            .map_err(|_| "public_configuration_body_unavailable")?;
        Ok((status, body))
    })
}

#[cfg(test)]
fn fetch_with(
    nonce: u64,
    fetch: impl FnMut(&str) -> Result<(u16, String), &'static str>,
) -> Result<Bootstrap, Failure> {
    fetch_seed_with(&format!("{nonce:016x}"), None, fetch)
}

fn fetch_seed_with(
    seed: &str,
    fields: Option<crate::client_dispatch::ClientDispatchReceipt>,
    mut fetch: impl FnMut(&str) -> Result<(u16, String), &'static str>,
) -> Result<Bootstrap, Failure> {
    let mut receipt = MetadataReceipt {
        request_seed_source: Some(if fields.is_some() {
            "local_client_resources"
        } else {
            "unverified_generated_value"
        }),
        client_resource_fields: fields,
        ..Default::default()
    };
    let result = (|| {
        if seed.len() != 16 || !seed.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Err("public_http_seed_shape_rejected");
        }
        receipt.requests_attempted += 1;
        let (status, body) = fetch(QUERY_URI)?;
        receipt.query_http_status = Some(status);
        if status != 200 {
            return Err("public_query_http_rejected");
        }
        let query: Value = serde_json::from_str(&body).map_err(|_| "public_query_json_invalid")?;
        receipt.query_return_code = query.get("retcode").and_then(Value::as_i64);
        if receipt.query_return_code != Some(0) {
            return Err("public_query_return_code_rejected");
        }
        let regions = query
            .get("region_list")
            .and_then(Value::as_array)
            .ok_or("public_region_list_invalid")?;
        receipt.region_entries = regions.len();
        if regions.len() != 1 {
            return Err("public_region_selection_ambiguous");
        }
        let dispatch = regions[0]
            .get("dispatch_url")
            .and_then(Value::as_str)
            .ok_or("public_gateway_url_missing")?;
        let gateway_uri = gateway_uri(dispatch, seed)?;
        receipt.gateway_url_allowed = true;
        receipt.requests_attempted += 1;
        let (status, body) = fetch(&gateway_uri)?;
        receipt.gateway_http_status = Some(status);
        if status != 200 {
            return Err("public_gateway_http_rejected");
        }
        let gateway: Value =
            serde_json::from_str(&body).map_err(|_| "public_gateway_json_invalid")?;
        // Original C++ Gateway has no return-code field; reject a code if present
        // unless it is a valid zero. No error message or body is exported.
        if let Some(code) = gateway.get("retcode") {
            receipt.gateway_return_code = code.as_i64();
            if receipt.gateway_return_code != Some(0) {
                return Err("public_gateway_return_code_rejected");
            }
        }
        let content = gateway
            .get("content")
            .and_then(Value::as_str)
            .ok_or("public_gateway_content_missing")?;
        derive_initial_configuration(content, &mut receipt)
    })();
    match result {
        Ok(initial_seed) => Ok(Bootstrap {
            initial_seed,
            receipt,
        }),
        Err(code) => Err(Failure {
            code,
            receipt: Box::new(receipt),
        }),
    }
}

fn gateway_uri(dispatch: &str, seed: &str) -> Result<String, &'static str> {
    let uri: Uri = dispatch.parse().map_err(|_| "public_gateway_url_invalid")?;
    let authority = uri.authority().ok_or("public_gateway_url_invalid")?;
    let host = uri
        .host()
        .ok_or("public_gateway_url_invalid")?
        .to_ascii_lowercase();
    if dispatch.contains('#')
        || uri.scheme_str() != Some("https")
        || authority.as_str().contains('@')
        || uri.port_u16().is_some_and(|port| port != 443)
        || !host
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || byte == b'.' || byte == b'-')
        || !(host == "juequling.com" || host.ends_with(".juequling.com"))
        || uri.path() != "/query_gateway"
        || uri.query().is_some()
    {
        return Err("public_gateway_url_not_allowed");
    }
    // Preserve the selected HTTP seed spelling; do not normalize client fields.
    // The generated-value mode remains an unverified historical candidate.
    // Both modes keep this separate from the initial pad seed and never persist it.
    Ok(format!(
        "{dispatch}?version=CNPRODWin3.2.0&rsa_ver=3&language=2&platform=3&seed={seed}&channel_id=1&sub_channel_id=1"
    ))
}

fn derive_initial_configuration(
    content: &str,
    receipt: &mut MetadataReceipt,
) -> Result<u64, &'static str> {
    let ciphertext = b64_decode(content);
    if ciphertext.is_empty() || !ciphertext.len().is_multiple_of(rsa::KEY_SIZE) {
        return Err("public_metadata_rsa_block_size_invalid");
    }
    let mut plaintext =
        rsa::decrypt_multi(&ciphertext).map_err(|_| "public_metadata_rsa_decode_failed")?;
    receipt.rsa_metadata_decoded = true;
    // Match the original trimTrailingForward: stop at the first NUL padding.
    // Do not guess the JSON boundary from a trailing brace in arbitrary bytes.
    if let Some(end) = plaintext.iter().position(|byte| *byte == 0) {
        plaintext.truncate(end);
    }
    let metadata: Value =
        serde_json::from_slice(&plaintext).map_err(|_| "public_metadata_json_invalid")?;
    receipt.metadata_json_decoded = true;
    observe_error_hints(&metadata, receipt);
    if let Some(code) = metadata.get("retcode") {
        receipt.metadata_return_code = code.as_i64();
        if receipt.metadata_return_code.is_none() {
            receipt.metadata_return_code_invalid_type = true;
            return Err("public_metadata_return_code_type_invalid");
        }
        if receipt.metadata_return_code != Some(0) {
            return Err("public_metadata_return_code_rejected");
        }
    }
    let encoded = metadata
        .get("client_secret_key")
        .and_then(Value::as_str)
        .ok_or("public_metadata_ec2b_missing")?;
    let seed =
        ec2b::derive_seed_from_b64(encoded).map_err(|_| "public_metadata_ec2b_decode_failed")?;
    receipt.ec2b_metadata_decoded = true;
    // TLS is checked by ureq. The inherited RSA/Ec2b path does not verify the
    // independent gateway signature; leave that fact false in this candidate.
    Ok(seed)
}

// Anonymous configuration errors can help diagnose a rejected public request.
// Export only bounded counters; never export the message, identifiers or nonce.
fn observe_error_hints(metadata: &Value, receipt: &mut MetadataReceipt) {
    let Some(message) = metadata.get("msg").and_then(Value::as_str) else {
        return;
    };
    receipt.metadata_message_characters = message.chars().count();
    let bounded = message
        .chars()
        .take(2048)
        .collect::<String>()
        .to_lowercase();
    let contains = |words: &[&str]| u8::from(words.iter().any(|word| bounded.contains(word)));
    receipt.metadata_version_hint_count = contains(&["version", "版本"]);
    receipt.metadata_parameter_hint_count = contains(&["parameter", "argument", "参数"]);
    receipt.metadata_nonce_hint_count = contains(&["nonce", "seed"]);
    receipt.metadata_signature_hint_count = contains(&["signature", "sign error", "签名"]);
    receipt.metadata_maintenance_hint_count = contains(&["maintenance", "维护", "停服"]);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn invalid_http_seed_stops_before_any_request() {
        let failed = fetch_seed_with("0123456789abcdeg", None, |_| {
            panic!("invalid seed must not issue an HTTP request")
        })
        .err()
        .unwrap();
        assert_eq!(failed.code, "public_http_seed_shape_rejected");
        assert_eq!(failed.receipt.requests_attempted, 0);
    }

    #[test]
    fn client_parameter_spelling_is_preserved_without_entering_receipts() {
        let fields = crate::client_dispatch::ClientDispatchReceipt {
            status: "client_request_fields_valid",
            same_json_object: true,
            expected_cn_version_matches: true,
            seed_is_16_hex: true,
            ..Default::default()
        };
        let mut calls = 0;
        let failed = fetch_seed_with("0123456789ABCDEF", Some(fields), |uri| {
            calls += 1;
            if calls == 1 {
                Ok((200, query("https://a.juequling.com/query_gateway")))
            } else {
                assert!(uri.contains("&seed=0123456789ABCDEF&"));
                Ok((200, "{\"retcode\":1}".into()))
            }
        })
        .err()
        .unwrap();
        assert_eq!(calls, 2);
        assert_eq!(
            failed.receipt.request_seed_source,
            Some("local_client_resources")
        );
        assert!(!serde_json::to_string(&failed.receipt)
            .unwrap()
            .contains("0123456789ABCDEF"));
    }
    use zzz_crypto::b64_encode;

    fn query(dispatch: &str) -> String {
        serde_json::json!({"retcode":0,"region_list":[{"dispatch_url":dispatch}]}).to_string()
    }

    fn encrypt(plain: &[u8]) -> String {
        let mut cipher = Vec::new();
        for chunk in plain.chunks(rsa::KEY_SIZE - 11) {
            cipher.extend(rsa::encrypt_block(chunk).unwrap());
        }
        b64_encode(&cipher)
    }

    #[test]
    fn second_hop_requires_https_official_host_and_exact_credential_free_path() {
        for rejected in [
            "http://a.juequling.com/query_gateway",
            "https://a.juequling.com.evil.example/query_gateway",
            "https://evil.example/query_gateway",
            "https://user:password@a.juequling.com/query_gateway",
            "https://a.juequling.com:444/query_gateway",
            "https://a.juequling.com/account/login",
            "https://a.juequling.com/query_gateway?token=synthetic",
            "https://a.juequling.com/query_gateway#fragment",
        ] {
            assert!(
                gateway_uri(rejected, "0000000000000007").is_err(),
                "{rejected}"
            );
        }
        let allowed =
            gateway_uri("https://a.juequling.com/query_gateway", "0000000000000007").unwrap();
        assert!(allowed.contains("&seed=0000000000000007&"));
        let agent = network_agent();
        assert_eq!(agent.config().max_redirects(), 0);
        assert!(agent.config().proxy().is_none());
        assert_eq!(agent.config().max_idle_connections(), 0);
        assert_eq!(agent.config().max_idle_connections_per_host(), 0);
    }

    #[test]
    fn rejected_or_ambiguous_regions_never_trigger_a_second_request() {
        for body in [
            "{",
            r#"{"retcode":1,"region_list":[]}"#,
            r#"{"retcode":0,"region_list":[]}"#,
            r#"{"retcode":0,"region_list":[{},{}]}"#,
            &query("https://evil.example/query_gateway"),
        ] {
            let mut calls = 0;
            let failed = fetch_with(7, |_| {
                calls += 1;
                Ok((200, body.into()))
            });
            assert!(failed.is_err());
            assert_eq!(calls, 1);
        }
    }

    #[test]
    fn rsa_and_ec2b_failures_remain_distinct_and_no_request_is_retried() {
        for (content, expected, rsa_decoded) in [
            (
                b64_encode(&[0; rsa::KEY_SIZE]),
                "public_metadata_rsa_decode_failed",
                false,
            ),
            (
                encrypt(br#"{"client_secret_key":"invalid"}"#),
                "public_metadata_ec2b_decode_failed",
                true,
            ),
        ] {
            let mut calls = 0;
            let failure = fetch_with(7, |_| {
                calls += 1;
                Ok((
                    200,
                    if calls == 1 {
                        query("https://a.juequling.com/query_gateway")
                    } else {
                        serde_json::json!({"content":content}).to_string()
                    },
                ))
            })
            .err()
            .unwrap();
            assert_eq!(calls, 2);
            assert_eq!(failure.code, expected);
            assert_eq!(failure.receipt.rsa_metadata_decoded, rsa_decoded);
            assert!(!failure.receipt.ec2b_metadata_decoded);
        }
    }

    #[test]
    fn metadata_rejections_keep_safe_codes_without_promoting_or_exporting_payloads() {
        for (code, expected, numeric, invalid_type) in [
            (
                serde_json::json!(-7),
                "public_metadata_return_code_rejected",
                Some(-7),
                false,
            ),
            (
                serde_json::json!("0"),
                "public_metadata_return_code_type_invalid",
                None,
                true,
            ),
            (
                Value::Null,
                "public_metadata_return_code_type_invalid",
                None,
                true,
            ),
        ] {
            let content = encrypt(
                serde_json::json!({
                    "retcode":code,
                    "msg":"version parameter nonce signature maintenance PRIVATE_REJECT_MARKER",
                    "client_secret_key":"PRIVATE_CONFIGURATION_MARKER"
                })
                .to_string()
                .as_bytes(),
            );
            let mut calls = 0;
            let failure = fetch_with(7, |_| {
                calls += 1;
                Ok((
                    200,
                    if calls == 1 {
                        query("https://a.juequling.com/query_gateway")
                    } else {
                        serde_json::json!({"content":content}).to_string()
                    },
                ))
            })
            .err()
            .unwrap();
            assert_eq!(calls, 2);
            assert_eq!(failure.code, expected);
            assert_eq!(failure.receipt.metadata_return_code, numeric);
            assert_eq!(
                failure.receipt.metadata_return_code_invalid_type,
                invalid_type
            );
            assert!(failure.receipt.rsa_metadata_decoded && failure.receipt.metadata_json_decoded);
            assert!(!failure.receipt.ec2b_metadata_decoded);
            assert_eq!(failure.receipt.metadata_version_hint_count, 1);
            assert_eq!(failure.receipt.metadata_parameter_hint_count, 1);
            assert_eq!(failure.receipt.metadata_nonce_hint_count, 1);
            assert_eq!(failure.receipt.metadata_signature_hint_count, 1);
            assert_eq!(failure.receipt.metadata_maintenance_hint_count, 1);
            let safe = serde_json::to_string(&failure.receipt).unwrap();
            assert!(!safe.contains("PRIVATE_REJECT_MARKER"));
            assert!(!safe.contains("PRIVATE_CONFIGURATION_MARKER"));
            assert!(!safe.contains(&content));
        }
    }

    #[test]
    fn error_hints_are_bounded_counts_and_never_export_a_message_or_nonce() {
        let mut receipt = MetadataReceipt::default();
        let message = format!("{}version PRIVATE_NONCE_MARKER", "x".repeat(2048));
        observe_error_hints(&serde_json::json!({"msg":message}), &mut receipt);
        assert_eq!(receipt.metadata_message_characters, message.chars().count());
        assert_eq!(receipt.metadata_version_hint_count, 0);
        let safe = serde_json::to_string(&receipt).unwrap();
        assert!(!safe.contains(&message));
        assert!(!safe.contains("PRIVATE_NONCE_MARKER"));
        observe_error_hints(
            &serde_json::json!({"msg":null}),
            &mut MetadataReceipt::default(),
        );
    }

    #[test]
    fn metadata_boundary_stops_at_first_nul_without_silently_trimming_other_garbage() {
        let mut receipt = MetadataReceipt::default();
        let content = encrypt(br#"{"retcode":-9}"#);
        assert_eq!(
            derive_initial_configuration(&content, &mut receipt),
            Err("public_metadata_return_code_rejected")
        );
        let mut padded = br#"{"retcode":-9}"#.to_vec();
        padded.extend(b"\0PRIVATE_PADDING_MARKER}");
        let mut padded_receipt = MetadataReceipt::default();
        assert_eq!(
            derive_initial_configuration(&encrypt(&padded), &mut padded_receipt),
            Err("public_metadata_return_code_rejected")
        );
        assert_eq!(padded_receipt.metadata_return_code, Some(-9));
        let mut unpadded = br#"{"retcode":-9}"#.to_vec();
        unpadded.extend(b"PRIVATE_GARBAGE_MARKER}");
        assert_eq!(
            derive_initial_configuration(&encrypt(&unpadded), &mut MetadataReceipt::default()),
            Err("public_metadata_json_invalid")
        );
    }

    #[test]
    fn synthetic_bootstrap_reuses_pinned_crypto_and_receipt_contains_no_configuration() {
        let mut blob = Vec::new();
        blob.extend(ec2b::MAGIC.to_be_bytes());
        blob.extend((ec2b::KEY_SIZE as u32).to_le_bytes());
        blob.extend([0x42; ec2b::KEY_SIZE]);
        blob.extend((ec2b::SEED_SIZE as u32).to_le_bytes());
        blob.extend([0x33; ec2b::SEED_SIZE]);
        let expected = ec2b::derive_seed(&blob).unwrap();
        let encoded = b64_encode(&blob);
        let content = encrypt(
            serde_json::json!({"retcode":0,"client_secret_key":encoded,"unused":"PRIVATE_SYNTHETIC_MARKER"})
                .to_string()
                .as_bytes(),
        );
        let mut calls = 0;
        let bootstrap = fetch_with(7, |_| {
            calls += 1;
            Ok((
                200,
                if calls == 1 {
                    query("https://a.juequling.com/query_gateway")
                } else {
                    serde_json::json!({"content":content}).to_string()
                },
            ))
        })
        .ok()
        .unwrap();
        assert_eq!(bootstrap.initial_seed, expected);
        assert_eq!(calls, 2);
        assert!(bootstrap.receipt.rsa_metadata_decoded && bootstrap.receipt.ec2b_metadata_decoded);
        assert_eq!(bootstrap.receipt.metadata_return_code, Some(0));
        assert!(!bootstrap.receipt.metadata_return_code_invalid_type);
        let serialized = serde_json::to_string(&bootstrap.receipt).unwrap();
        for private in [
            encoded,
            content,
            expected.to_string(),
            "PRIVATE_SYNTHETIC_MARKER".into(),
        ] {
            assert!(!serialized.contains(&private));
        }
        assert!(!bootstrap.receipt.signature_verified);
        assert!(!bootstrap.receipt.response_payload_saved);
        assert!(!bootstrap.receipt.credentials_used);
    }
}
