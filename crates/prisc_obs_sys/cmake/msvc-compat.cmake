# obs-express compatibility shim for newer MSVC toolsets, injected into the
# Windows OBS configure via CMAKE_PROJECT_INCLUDE (runs right after OBS's
# top-level project(), so the definitions reach every target below it).
#
# Why: libobs-winrt uses /await with <experimental/coroutine>. MSVC 14.51+
# (VS 2026, e.g. the windows-11-arm runner image) turns that deprecation into
# a hard error (STL1011) that OBS 32.1.2 predates. The header still works;
# the documented macro below keeps it available.
add_compile_definitions(_SILENCE_EXPERIMENTAL_COROUTINE_DEPRECATION_WARNINGS)
