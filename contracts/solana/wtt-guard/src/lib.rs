#![no_std]

use core::{ptr, slice};

const MAX_ACCOUNT_COUNT: usize = 10;
const DATA_GROWTH: usize = 10_240;
const MAX_ACCOUNT_DATA: usize = 10 * 1024 * 1024;
const LOADER: [u8; 32] = base58(b"BPFLoaderUpgradeab1e11111111111111111111111");
const RENT: [u8; 32] = base58(b"SysvarRent111111111111111111111111111111111");
const CLOCK: [u8; 32] = base58(b"SysvarC1ock11111111111111111111111111111111");
const METADATA: [u8; 32] = base58(b"metaqbxxUerdq28cj1RbAWkYQm3ybzjb6a8bt518x1s");
const SYSVAR_OWNER: [u8; 32] = base58(b"Sysvar1111111111111111111111111111111111111");
const JUPITER: [u8; 32] = base58(b"JUP6LkbZbjS1jKKwapdHNy74zcZ3tLUZoi5QNyVTaV4");
const WHIRLPOOL: [u8; 32] = base58(b"whirLbMiicVdio4qvUfM5KAg6Ct8VwpYzGff3uctyCc");
const TOKEN: [u8; 32] = base58(b"TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA");
const WRAPPED_SOL: [u8; 32] = base58(b"So11111111111111111111111111111111111111112");

#[derive(Clone, Copy)]
struct Account<'a> {
    key: &'a [u8],
    owner: &'a [u8],
    data: &'a [u8],
    executable: bool,
    writable: bool,
    signer: bool,
    lamports: u64,
}

const EMPTY: Account<'static> = Account {
    key: &[], owner: &[], data: &[], executable: false, writable: false, signer: false, lamports: 0,
};

#[repr(C)]
struct HashSlice { address: *const u8, length: u64 }

extern "C" {
    fn sol_sha256(values: *const HashSlice, count: u64, result: *mut u8) -> u64;
}

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! { loop {} }

// Solana aligned account-input ABI; provenance and bounds are in AGENTS.md.
unsafe fn decode<'a>(input: *const u8) -> Option<([Account<'a>; MAX_ACCOUNT_COUNT], usize, &'a [u8])> {
    if input.is_null() { return None; }
    let count = ptr::read_unaligned(input as *const u64);
    if count != 1 && count != 2 && count != 4 && count != 6 && count != 8 && count != 10 { return None; }
    let mut offset = 8usize;
    let mut accounts = [EMPTY; MAX_ACCOUNT_COUNT];
    for account in accounts[..count as usize].iter_mut() {
        if *input.add(offset) != 255 { return None; }
        let signer = *input.add(offset + 1);
        let writable = *input.add(offset + 2);
        let executable = *input.add(offset + 3);
        if signer > 1 || writable > 1 || executable > 1 { return None; }
        offset += 8;
        let key = slice::from_raw_parts(input.add(offset), 32); offset += 32;
        let owner = slice::from_raw_parts(input.add(offset), 32); offset += 32;
        let lamports = ptr::read_unaligned(input.add(offset) as *const u64); offset += 8;
        let length = ptr::read_unaligned(input.add(offset) as *const u64);
        if length > MAX_ACCOUNT_DATA as u64 { return None; }
        offset += 8;
        let data = slice::from_raw_parts(input.add(offset), length as usize);
        offset = offset.checked_add(length as usize)?.checked_add(DATA_GROWTH)?;
        offset = offset.checked_add(7)? & !7;
        offset = offset.checked_add(8)?;
        *account = Account { key, owner, data, executable: executable != 0, writable: writable != 0, signer: signer != 0, lamports };
    }
    let size = ptr::read_unaligned(input.add(offset) as *const u64);
    if (count == 1 && size != 18) || (count == 2 && size != 97) || (count == 4 && size != 50) || (count == 6 && size != 82)
        || (count == 8 && size != 90) || (count == 10 && size != 122) { return None; }
    Some((accounts, count as usize, slice::from_raw_parts(input.add(offset + 8), size as usize)))
}

fn u64_le(value: &[u8]) -> u64 { u64::from_le_bytes(value[..8].try_into().unwrap()) }
fn u32_le(value: &[u8]) -> u32 { u32::from_le_bytes(value[..4].try_into().unwrap()) }

fn program_matches(program: Account<'_>, data: Account<'_>, expected_hash: &[u8], current_slot: u64) -> bool {
    if program.owner != LOADER || data.owner != LOADER
        || !program.executable || data.executable || program.writable || data.writable || program.signer || data.signer
        || program.data.len() != 36 || program.data[..4] != [2, 0, 0, 0] || &program.data[4..] != data.key
        || data.data.len() <= 45 || data.data[..4] != [3, 0, 0, 0] || data.data[12] > 1
        || u64_le(&data.data[4..12]) >= current_slot { return false; }
    let code = &data.data[45..];
    let segment = HashSlice { address: code.as_ptr(), length: code.len() as u64 };
    let mut digest = [0u8; 32];
    unsafe { sol_sha256(&segment, 1, digest.as_mut_ptr()) == 0 && digest.as_slice() == expected_hash }
}

fn validate(accounts: &[Account<'_>], instruction: &[u8]) -> bool {
    if accounts.len() == 1 && instruction.len() == 18 && instruction[0] == 6 {
        let rent = accounts[0];
        return rent.key == RENT && rent.owner == SYSVAR_OWNER && !rent.executable && !rent.writable && !rent.signer
            && rent.data == &instruction[1..18];
    }
    if accounts.len() == 2 && instruction.len() == 97 && instruction[0] == 5 {
        let token = accounts[0]; let owner = accounts[1];
        if token.owner != TOKEN || token.executable || token.signer || !token.writable || token.data.len() != 165
            || token.key != &instruction[1..33] || &token.data[..32] != &instruction[33..65]
            || &token.data[32..64] != &instruction[65..97] || owner.key != &instruction[65..97]
            || owner.executable || !owner.signer || !owner.writable || token.key == owner.key
            || !matches!(token.data[108], 1 | 2) || u64_le(&token.data[64..72]) != 0
            || u32_le(&token.data[72..76]) > 1 || u32_le(&token.data[109..113]) > 1
            || u32_le(&token.data[129..133]) > 1 { return false; }
        let native = u32_le(&token.data[109..113]) == 1;
        if native != (&token.data[..32] == WRAPPED_SOL)
            || (native && token.lamports != u64_le(&token.data[113..121])) { return false; }
        return u32_le(&token.data[129..133]) == 0 || &token.data[133..165] == owner.key;
    }
    if accounts.len() == 6 && instruction.len() == 82 && instruction[0] == 4 {
        let rent = accounts[0]; let clock = accounts[1];
        return rent.key == RENT && rent.owner == SYSVAR_OWNER && !rent.executable && !rent.writable && !rent.signer
            && rent.data == &instruction[1..18]
            && clock.key == CLOCK && clock.owner == SYSVAR_OWNER && !clock.executable && !clock.writable && !clock.signer
            && clock.data.len() == 40 && accounts[2].key == JUPITER && accounts[4].key == WHIRLPOOL
            && program_matches(accounts[2], accounts[3], &instruction[18..50], u64_le(clock.data))
            && program_matches(accounts[4], accounts[5], &instruction[50..82], u64_le(clock.data));
    }
    if accounts.len() == 4 && instruction.len() == 50 && instruction[0] == 3 {
        let rent = accounts[0]; let clock = accounts[1];
        return rent.key == RENT && rent.owner == SYSVAR_OWNER && !rent.executable && !rent.writable && !rent.signer
            && rent.data == &instruction[1..18]
            && clock.key == CLOCK && clock.owner == SYSVAR_OWNER && !clock.executable && !clock.writable && !clock.signer
            && clock.data.len() == 40 && accounts[2].key == METADATA
            && program_matches(accounts[2], accounts[3], &instruction[18..50], u64_le(clock.data));
    }
    let registration = accounts.len() == 10 && instruction.len() == 122 && instruction[0] == 2;
    if !registration && !(accounts.len() == 8 && instruction.len() == 90 && instruction[0] == 1) { return false; }
    let bridge = accounts[0]; let rent = accounts[1]; let clock = accounts[2];
    if bridge.owner != accounts[3].key || bridge.executable || bridge.signer
        || bridge.data.len() != 24 || u64_le(&bridge.data[16..24]) != u64_le(&instruction[1..9])
        || rent.key != RENT || rent.owner != SYSVAR_OWNER || rent.executable || rent.writable || rent.signer
        || rent.data != &instruction[9..26]
        || clock.key != CLOCK || clock.owner != SYSVAR_OWNER || clock.executable || clock.writable || clock.signer
        || clock.data.len() != 40 || accounts[7].owner != accounts[5].key || accounts[7].executable || accounts[7].signer
        || !matches!(accounts[7].data.len(), 32 | 137)
        || &accounts[7].data[..32] != accounts[3].key
        || (accounts[7].data.len() == 137 && accounts[7].data[32] != 0) { return false; }
    let slot = u64_le(clock.data);
    program_matches(accounts[3], accounts[4], &instruction[26..58], slot)
        && program_matches(accounts[5], accounts[6], &instruction[58..90], slot)
        && (!registration || (accounts[8].key == METADATA
            && program_matches(accounts[8], accounts[9], &instruction[90..122], slot)))
}

#[no_mangle]
pub unsafe extern "C" fn entrypoint(input: *const u8) -> u64 {
    match decode(input) {
        Some((accounts, count, instruction)) if validate(&accounts[..count], instruction) => 0,
        _ => 1,
    }
}

const fn base58(text: &[u8]) -> [u8; 32] {
    let alphabet = b"123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    let mut bytes = [0u8; 32]; let mut index = 0;
    while index < text.len() {
        let mut digit = 0;
        while digit < alphabet.len() && alphabet[digit] != text[index] { digit += 1; }
        assert!(digit < alphabet.len());
        let mut carry = digit as u32; let mut position = 32;
        while position > 0 {
            position -= 1; carry += bytes[position] as u32 * 58;
            bytes[position] = (carry & 255) as u8; carry >>= 8;
        }
        assert!(carry == 0); index += 1;
    }
    bytes
}
