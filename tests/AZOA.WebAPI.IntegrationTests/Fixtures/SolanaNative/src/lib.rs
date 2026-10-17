#![no_std]

#[panic_handler]
fn panic(_info: &core::panic::PanicInfo) -> ! {
    loop {}
}

// Distinct executable revisions both succeed without touching caller accounts.
#[no_mangle]
pub extern "C" fn entrypoint(input: *const u8) -> u64 {
    #[cfg(feature = "revision-two")]
    unsafe {
        core::ptr::read_volatile(input);
    }
    #[cfg(not(feature = "revision-two"))]
    let _ = input;
    0
}
