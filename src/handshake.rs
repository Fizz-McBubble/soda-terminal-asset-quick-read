//! Counter-only observation of the pinned token extraction algorithm.
//! This never returns plaintext, field values, RSA material, or session keys.

use crate::snapshot::TokenBodyCounters;
use zzz_crypto::{b64_decode, rsa};
use zzz_wire::proto::{Message, Value};

pub(crate) fn observe_token_body(body: &[u8], counters: &mut TokenBodyCounters) {
    let Ok(message) = Message::decode(body) else {
        counters.protobuf_parse_failures += 1;
        return;
    };
    counters.protobuf_parse_successes += 1;
    for field in &message.fields {
        let Value::LengthDelimited(bytes) = &field.value else {
            continue;
        };
        counters.length_delimited_fields += 1;
        let Ok(text) = std::str::from_utf8(bytes) else {
            counters.non_utf8_fields += 1;
            continue;
        };
        // Match the pinned extractor's permissive Base64 reader exactly.
        let decoded = b64_decode(text);
        if decoded.len() != rsa::KEY_SIZE {
            counters.base64_non_rsa_fields += 1;
            continue;
        }
        counters.rsa_block_candidates += 1;
        let Ok(plain) = rsa::decrypt_block(&decoded) else {
            counters.rsa_decode_failures += 1;
            continue;
        };
        if plain.len() != 8 {
            counters.rsa_plain_length_mismatches += 1;
            continue;
        }
        counters.eight_byte_candidates += 1;
        // Stop at the first usable candidate, as the pinned extractor does.
        return;
    }
    counters.frames_without_usable_candidate += 1;
}

#[cfg(test)]
mod tests {
    use super::*;
    use zzz_crypto::b64_encode;
    use zzz_scan::{Session, SessionError};
    use zzz_wire::proto::Field;

    fn encoded_field(bytes: &[u8]) -> Field {
        Field::new(9, Value::LengthDelimited(b64_encode(bytes).into_bytes()))
    }

    fn observe(fields: Vec<Field>) -> (Vec<u8>, TokenBodyCounters) {
        let body = Message { fields }.encode();
        let mut counters = TokenBodyCounters::default();
        observe_token_body(&body, &mut counters);
        (body, counters)
    }

    #[test]
    fn bad_protobuf_is_distinct_from_a_valid_body_without_a_candidate() {
        let mut invalid = TokenBodyCounters::default();
        observe_token_body(&[0], &mut invalid);
        assert!(matches!(
            Session::extract_server_rand_key(&[0]),
            Err(SessionError::Protobuf(_))
        ));
        assert_eq!(invalid.protobuf_parse_failures, 1);
        assert_eq!(invalid.frames_without_usable_candidate, 0);

        let (body, no_candidate) = observe(vec![
            Field::new(1, Value::Varint(0)),
            Field::new(2, Value::LengthDelimited(vec![0xFF])),
            encoded_field(&[1, 2, 3]),
        ]);
        assert_eq!(
            Session::extract_server_rand_key(&body),
            Err(SessionError::ServerRandKeyNotFound)
        );
        assert_eq!(no_candidate.protobuf_parse_successes, 1);
        assert_eq!(no_candidate.length_delimited_fields, 2);
        assert_eq!(no_candidate.non_utf8_fields, 1);
        assert_eq!(no_candidate.base64_non_rsa_fields, 1);
        assert_eq!(no_candidate.rsa_block_candidates, 0);
        assert_eq!(no_candidate.frames_without_usable_candidate, 1);
    }

    #[test]
    fn rsa_failure_is_distinct_from_a_decrypted_wrong_length() {
        let (invalid_body, invalid) = observe(vec![encoded_field(&[0; rsa::KEY_SIZE])]);
        assert_eq!(
            Session::extract_server_rand_key(&invalid_body),
            Err(SessionError::ServerRandKeyNotFound)
        );
        assert_eq!(invalid.rsa_block_candidates, 1);
        assert_eq!(invalid.rsa_decode_failures, 1);
        assert_eq!(invalid.rsa_plain_length_mismatches, 0);

        let (short_body, short) = observe(vec![encoded_field(
            &rsa::encrypt_block(&[0x42; 7]).unwrap(),
        )]);
        assert_eq!(
            Session::extract_server_rand_key(&short_body),
            Err(SessionError::ServerRandKeyNotFound)
        );
        assert_eq!(short.rsa_block_candidates, 1);
        assert_eq!(short.rsa_decode_failures, 0);
        assert_eq!(short.rsa_plain_length_mismatches, 1);
        assert_eq!(short.frames_without_usable_candidate, 1);
    }

    #[test]
    fn malformed_candidates_are_skipped_and_a_later_usable_candidate_stops_observation() {
        let synthetic = [0x42; 8];
        let (body, counters) = observe(vec![
            encoded_field(&[0; rsa::KEY_SIZE]),
            encoded_field(&rsa::encrypt_block(&[0x42; 7]).unwrap()),
            encoded_field(&rsa::encrypt_block(&synthetic).unwrap()),
            encoded_field(&[0; rsa::KEY_SIZE]),
        ]);
        assert_eq!(
            Session::extract_server_rand_key(&body),
            Ok(u64::from_le_bytes(synthetic))
        );
        assert_eq!(counters.rsa_block_candidates, 3);
        assert_eq!(counters.rsa_decode_failures, 1);
        assert_eq!(counters.rsa_plain_length_mismatches, 1);
        assert_eq!(counters.eight_byte_candidates, 1);
        assert_eq!(counters.frames_without_usable_candidate, 0);
    }
}
