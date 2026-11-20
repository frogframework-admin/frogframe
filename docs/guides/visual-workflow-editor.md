# Visual Workflow Editor

The workflow editor (`/admin/workflows/{name}/editor`, System Manager only) has a **Flow designer** canvas above the state and transition lists. Both edit the same draft, and nothing changes for documents until the workflow is published.

## Canvas actions

| Action | How |
|---|---|
| Move a state | Drag it, or select it and use the arrow keys (Shift = larger steps). Positions snap to a 10-unit grid. |
| Add a transition | Drag the dot on the right edge of a state onto the target state. The action is named "Move to {target}" and starts with the roles of the source state's other actions. Rename it in the inspector. |
| Add a state | Double-click empty space, or use **Add state**. |
| Edit | Click a state or an arrow. The inspector on the right shows its settings: name, document status, colour and initial state; or action name, roles, condition and required comment. |
| Undo | **Undo** or Ctrl+Z (up to 50 steps). |
| Auto layout | Forgets the saved positions and arranges the states automatically again. |

## Design warnings

The canvas flags problems that do not block publishing but are usually mistakes:

- a state that no action leads to from the initial state;
- an editable (draft) state with no action out of it, so documents get stuck there;
- an action that no role may run;
- the same action name defined twice from one state.

Click a warning to select the state or transition it refers to.

## Positions in the workflow JSON

State positions are stored as optional `x` and `y` on each state:

```json
{ "name": "Submitted", "docStatus": 1, "tone": "pending", "x": 220, "y": 60 }
```

The workflow engine ignores them. When a published version only moves states, *Check changes* reports a single **Safe** change ("Diagram layout changed"), and publishing it does not affect documents. Workflows without positions, including all existing ones, are laid out automatically as before. Read-only diagrams elsewhere (DocType editor, metadata admin) use the saved positions too.
