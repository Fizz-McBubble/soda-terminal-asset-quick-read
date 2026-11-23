use zzz_gamedata::GameData;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum PotentialMapping {
    Verified(u32),
    ReviewedUnavailable,
    Unsupported,
}

impl PotentialMapping {
    pub(crate) fn field(self) -> Option<u32> {
        match self {
            Self::Verified(number) => Some(number),
            Self::ReviewedUnavailable | Self::Unsupported => None,
        }
    }
}

/// The three approved 3.2 local controls matched field 665 to independent
/// PotentialDetail rows. The 3.3 upstream explicitly writes potential as zero:
/// datamine has no `agentInfo.core`, the old field name is absent from the new
/// agent schema, and neither the C++ source nor a reviewed snapshot identifies a
/// replacement. Keep 3.3 absent rather than manufacturing zero or guessing a
/// high-numbered field.
pub(crate) fn potential_mapping(data: &GameData) -> PotentialMapping {
    if data.datamine.cmd_get_avatar_data_sc_rsp == 7398 && data.datamine.agent_data.agents == 12 {
        let response = data
            .proto_entries
            .iter()
            .filter(|row| row.cmd_id == Some(7398))
            .collect::<Vec<_>>();
        let container = response
            .first()
            .and_then(|entry| {
                let fields = entry
                    .fields
                    .iter()
                    .filter(|field| field.number == 12)
                    .collect::<Vec<_>>();
                match fields.as_slice() {
                    [field] => Some(*field),
                    _ => None,
                }
            })
            .filter(|field| field.type_name == "CCLLIEJENHM" && field.repeated);
        let schemas = data
            .proto_entries
            .iter()
            .filter(|row| row.name == "CCLLIEJENHM")
            .collect::<Vec<_>>();
        if response.len() == 1
            && container.is_some()
            && schemas.len() == 1
            && !schemas[0]
                .fields
                .iter()
                .any(|field| field.name == "ENNAMBHGLGD")
        {
            return PotentialMapping::ReviewedUnavailable;
        }
    }
    if data.datamine.cmd_get_avatar_data_sc_rsp != 2470 || data.datamine.agent_data.agents != 2 {
        return PotentialMapping::Unsupported;
    }
    let response = data
        .proto_entries
        .iter()
        .filter(|row| row.cmd_id == Some(2470))
        .collect::<Vec<_>>();
    if response.len() != 1 {
        return PotentialMapping::Unsupported;
    }
    let container = response[0]
        .fields
        .iter()
        .filter(|field| field.number == 2)
        .collect::<Vec<_>>();
    if container.len() != 1 || container[0].type_name != "OCNCGOBPHLA" || !container[0].repeated {
        return PotentialMapping::Unsupported;
    }
    let schemas = data
        .proto_entries
        .iter()
        .filter(|row| row.name == "OCNCGOBPHLA")
        .collect::<Vec<_>>();
    if schemas.len() != 1 {
        return PotentialMapping::Unsupported;
    }
    let fields = schemas[0]
        .fields
        .iter()
        .filter(|field| field.number == 665)
        .collect::<Vec<_>>();
    if fields.len() != 1 {
        return PotentialMapping::Unsupported;
    }
    let field = fields[0];
    if field.name == "ENNAMBHGLGD"
        && field.type_name == "uint32"
        && field.is_native_type
        && !field.is_enum
        && !field.repeated
        && field.xor_value == Some(1715)
    {
        PotentialMapping::Verified(665)
    } else {
        PotentialMapping::Unsupported
    }
}

#[cfg(test)]
#[path = "potential_tests.rs"]
mod tests;
