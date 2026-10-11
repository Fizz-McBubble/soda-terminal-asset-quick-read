use zzz_gamedata::GameData;

/// The three approved local controls matched these IDs to independent
/// PotentialDetail rows: Ellen/Koleda level 6 and Rina level 5. Decode only the
/// exact pinned 3.2 record; the consumer joins the ID to a reviewed static row.
pub(crate) fn verified_potential_field(data: &GameData) -> Option<u32> {
    if data.datamine.cmd_get_avatar_data_sc_rsp != 2470 || data.datamine.agent_data.agents != 2 {
        return None;
    }
    let response = data
        .proto_entries
        .iter()
        .filter(|row| row.cmd_id == Some(2470))
        .collect::<Vec<_>>();
    if response.len() != 1 {
        return None;
    }
    let container = response[0]
        .fields
        .iter()
        .filter(|field| field.number == 2)
        .collect::<Vec<_>>();
    if container.len() != 1 || container[0].type_name != "OCNCGOBPHLA" || !container[0].repeated {
        return None;
    }
    let schemas = data
        .proto_entries
        .iter()
        .filter(|row| row.name == "OCNCGOBPHLA")
        .collect::<Vec<_>>();
    if schemas.len() != 1 {
        return None;
    }
    let fields = schemas[0]
        .fields
        .iter()
        .filter(|field| field.number == 665)
        .collect::<Vec<_>>();
    if fields.len() != 1 {
        return None;
    }
    let field = fields[0];
    (field.name == "ENNAMBHGLGD"
        && field.type_name == "uint32"
        && field.is_native_type
        && !field.is_enum
        && !field.repeated
        && field.xor_value == Some(1715))
    .then_some(665)
}

#[cfg(test)]
#[path = "potential_tests.rs"]
mod tests;
