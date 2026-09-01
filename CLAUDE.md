# CLAUDE.md

Guidance for Claude Code (claude.ai/code) in this repo.

See **[AGENTS.md](AGENTS.md)** — it is the canonical guide (project layout, the
`mise run ci` gate, coverage floors, docs sync, packing and releasing, and the
jj workflow). Start there.

Claude-specific notes:

- Always pass `-m` to `jj describe` / `jj commit`. Without it jj opens `$EDITOR`,
  which is a GUI application on these machines, and hangs.
- Run `mise run ci` before reporting work complete. Do not infer a green from a
  partial build.
