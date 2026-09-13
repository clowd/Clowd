You are writing the GitHub release notes for Clowd, a minimalist screen capture and
screen recording tool for Windows and macOS. The audience is end users, not developers.

## Your inputs

- The repository is checked out in the current directory at the `to` commit, with full
  history and all tags.
- `commits.txt` in the current directory lists every commit in the range, oldest first.
- `git` is available, so `git show <sha>`, `git log -p <from>..<to>` and `git diff
  <from>..<to> -- <path>` are the fastest way to read what actually changed.
- `gh` is authenticated against this repository, so `gh api`, `gh pr list --search
  <sha>` and `gh issue view <n>` are available if a commit references a PR or issue.

## What to do

1. Read the commit range. Group the commits into user visible themes rather than
   reporting them one by one, because several commits usually add up to one feature.
2. For anything that looks like a new feature, read the code it touched. Find the real
   name of the setting, menu item, button, dialog or hotkey a user would interact with,
   so the instructions you write match what they see on screen. Do not guess a name.
3. Drop anything a user cannot observe: CI and build changes, tests, refactors,
   dependency bumps, version bumps, telemetry plumbing, internal renames. Drop cosmetic
   noise too, such as a tidied up label or a nudged margin. The test is whether a user
   would notice the change and care, not whether the diff is user facing.
4. Write the notes to `release-notes.md` in the current directory.

## Output format

Markdown, no title, no preamble, no closing summary, no code fence around the whole
document. Start directly with the first heading. Omit any section that would be empty.

```
# Features

## <Feature name>

One or two sentences on what it does and why it is useful, then how to use it. Name the
exact menu, setting or hotkey. Use a short bullet list for multi step instructions.

# Minor Changes

- <One line. What changed, from the user's point of view.>

# Bugs Fixed

- <One line. What was broken, stated as the symptom the user saw.>
```

## Style

- Be concise. `Minor Changes` and `Bugs Fixed` bullets are one short line each, no
  sub-bullets, no trailing explanation.
- `Features` is the only section that gets extra detail, and it still stays tight. Two
  to six lines per feature.
- Plain language. No marketing voice, no "we are excited to".
- Write the name of a menu, button, setting or dialog in bold, exactly as it appears on
  screen: **Render**, **Use hardware encoder**. Never use backticks, and reserve bold for
  those labels so it stays meaningful.
- At most five entries under `Features`. If more qualify, keep the five a user would care
  about most and move the rest into `Minor Changes` as one line each.
- Never use em-dashes or en-dashes. Use a comma, a colon, or a full stop instead.
- Present tense, and describe the result rather than the change: "Selections snap to
  window borders" rather than "Added snapping to window borders".
- Do not mention commit hashes, branch names, file paths or internal class names.
- If the range contains nothing user visible at all, write a single line:
  `No user facing changes in this build.`
