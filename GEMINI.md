# Project Guidelines & Preferences

## Language & Communication Defaults
- **Language Default**: All UI labels, descriptions, captions, tooltips, error messages, placeholders, and source code comments must default to **English** unless the user explicitly requests another language for a specific feature.
- **Documentation**: All documentation files (Markdown, DocFX, README, guides, runbooks, RFCs, architectural specs, and TODO lists) must be written in **English**.
- **Code Comments**: Keep code comments and docstrings in English.
- **UI Elements**: Ensure UI text is clean, professional, and grammatically correct English.

## Build & Deployment Workflow
- **Automatic Redeploy**: After building and verifying changes successfully, run `./redeploy.ps1` to rebuild and update the local Docker application container (`frogframe-app`).
- **Git Commit Policy**: Do **NOT** commit or push changes automatically (`git commit` / `git push`). Always wait for explicit instruction or confirmation from the user before committing. Automatic redeployment via `./redeploy.ps1` is allowed and encouraged.


