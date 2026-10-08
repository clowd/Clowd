# obs-express override of OBS's Linux helpers (obs-studio/cmake/linux/helpers.cmake).
#
# obs-sys passes this directory as CMAKE_MODULE_PATH on Linux; OBS appends its
# own module directories after it, so the top-level `include(helpers)` lands
# here. This file loads OBS's real helpers, then wraps add_obs_plugin.
#
# Why: OBS 32.2.2 moved frontend-tools from the frontend tree into plugins/
# and adds it on Linux unconditionally, with `find_package(Qt6 REQUIRED)` —
# ENABLE_FRONTEND=OFF no longer skips it. obs-express never builds it (Linux
# builds an explicit target list), but the configure would still need Qt6
# development files. Windows and macOS get Qt6 from obs-deps, so only Linux
# needs this. frontend-tools gets the same dummy, disabled target OBS creates
# for a plugin that is unavailable on the platform.
include_guard(GLOBAL)

include("${CMAKE_SOURCE_DIR}/cmake/linux/helpers.cmake")

# Redefining a command keeps the previous definition callable as
# _add_obs_plugin.
function(add_obs_plugin target)
  if(target STREQUAL "frontend-tools" AND NOT ENABLE_FRONTEND)
    add_custom_target(${target} COMMENT "Dummy target for unavailable module ${target}")
    target_disable(${target})
  else()
    _add_obs_plugin(${ARGV})
  endif()
endfunction()
