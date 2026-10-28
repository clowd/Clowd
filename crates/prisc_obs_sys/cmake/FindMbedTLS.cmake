# prisc_recorder override of OBS's FindMbedTLS (obs-studio/cmake/finders/FindMbedTLS.cmake).
#
# On Linux prisc_obs_sys links a static Mbed TLS built from source (see
# linux_ensure_mbedtls in crates/prisc_obs_sys/build.rs) as one merged archive, which
# OBS's finder accepts in its single-library form. Reaching that form runs
# check_symbol_exists(), which OBS's finder calls without including the
# CheckSymbolExists module (a system Mbed TLS always takes the other branch,
# so upstream never hits it). Include it, then delegate unchanged.

include(CheckSymbolExists)
include("${CMAKE_SOURCE_DIR}/cmake/finders/FindMbedTLS.cmake")
