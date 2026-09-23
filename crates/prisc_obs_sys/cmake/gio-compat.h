/* Force-included (via FindGio.cmake in this directory) into linux-pipewire
 * when the build machine's GLib is older than 2.76.
 *
 * Backfills g_clear_fd(), which GLib 2.76 added as a static inline in
 * <glib-unix.h> and linux-pipewire uses. Mirrors GLib's own definition: close
 * *fd_ptr if it is valid, always leave it -1, and report close errors through
 * g_close() (available since 2.36). */
#pragma once

#include <glib.h>
#include <glib-unix.h>
#include <glib/gstdio.h> /* g_close() */

#if !GLIB_CHECK_VERSION(2, 76, 0)
static inline gboolean g_clear_fd(int *fd_ptr, GError **error)
{
	int fd = *fd_ptr;

	*fd_ptr = -1;
	if (fd < 0)
		return TRUE;
	return g_close(fd, error);
}
#endif
