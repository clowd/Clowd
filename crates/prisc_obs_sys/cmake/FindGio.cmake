# obs-express override of OBS's FindGio (obs-studio/cmake/finders/FindGio.cmake).
#
# obs-sys passes this directory as CMAKE_MODULE_PATH on Linux; OBS appends its
# own finder directories after it, so this file wins and delegates to OBS's.
#
# Why: the reference build image (tools/linux-build/Dockerfile, manylinux_2_34
# = AlmaLinux 9) ships GLib 2.68, but OBS 32.1.2's linux-pipewire asks for
# `find_package(Gio 2.76 REQUIRED)`. The only 2.76 API it uses is
# g_clear_fd(), a static inline in <glib-unix.h> — nothing from the 2.76
# *library*. So the real version check is done here against 2.68, and with
# GLib < 2.76 linux-pipewire force-includes gio-compat.h, which backfills
# g_clear_fd(). The built binaries then need only GLib >= 2.68 at runtime,
# which every glibc-2.34+ distro has.
#
# The force-include is scoped to linux-pipewire, not to every gio::gio
# consumer: libobs links gio too, and pulling <glib.h> (and with it
# <pthread.h>) into its sources ahead of their own `#define _GNU_SOURCE`
# hides pthread_setname_np and friends.

set(_obs_express_gio_min 2.68)
set(_obs_express_gio_requested "${Gio_FIND_VERSION}")
# Run OBS's finder without the caller's version so its own
# find_package_handle_standard_args does not reject 2.68..2.75.
unset(Gio_FIND_VERSION)
unset(Gio_FIND_VERSION_MAJOR)
unset(Gio_FIND_VERSION_MINOR)
unset(Gio_FIND_VERSION_PATCH)
unset(Gio_FIND_VERSION_COUNT)
include("${CMAKE_SOURCE_DIR}/cmake/finders/FindGio.cmake")
set(Gio_FIND_VERSION "${_obs_express_gio_requested}")

if(Gio_FOUND)
  if(Gio_VERSION VERSION_LESS _obs_express_gio_min)
    message(FATAL_ERROR "GLib/Gio ${Gio_VERSION} is too old: obs-express needs >= ${_obs_express_gio_min}.")
  endif()
  if(Gio_VERSION VERSION_LESS 2.76 AND TARGET gio::gio)
    get_property(_obs_express_gio_compat_set TARGET gio::gio PROPERTY OBS_EXPRESS_GIO_COMPAT)
    if(NOT _obs_express_gio_compat_set)
      message(STATUS "obs-express: Gio ${Gio_VERSION} < 2.76, backfilling g_clear_fd() for linux-pipewire")
      set_property(TARGET gio::gio PROPERTY OBS_EXPRESS_GIO_COMPAT TRUE)
      set_property(
        TARGET gio::gio
        APPEND
        PROPERTY
          INTERFACE_COMPILE_OPTIONS
            "$<$<STREQUAL:$<TARGET_PROPERTY:NAME>,linux-pipewire>:SHELL:-include ${CMAKE_CURRENT_LIST_DIR}/gio-compat.h>"
      )
    endif()
    unset(_obs_express_gio_compat_set)
  endif()
endif()

unset(_obs_express_gio_min)
unset(_obs_express_gio_requested)
