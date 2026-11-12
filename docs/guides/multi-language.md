# Multi-Language UI (Indonesian / English)

FrogFrame shows its screens in **Bahasa Indonesia** or **English**. Each user picks a language with the **ID | EN**
switch in the top bar; the choice is kept in a cookie and in the user's preferences, so it follows the user to other
devices after signing in.

Translations work like Frappe's: **the text itself is the key**. A label, button or message is written once, in any
language, and translated when it is shown. Nothing is renamed in metadata and stored values never change.

```
"Save draft"            → id: "Simpan draf"          (framework dictionary)
"Pengajuan Pembelian"   → en: "Purchase Request"     (application dictionary)
"Prioritas wajib diisi" → en: "Priority is required" (template "{0} wajib diisi" + label "Prioritas")
```

## 1. Where translations come from

From lowest to highest priority:

| Source | Where | Who |
|---|---|---|
| Framework dictionaries | `src/Framework.Web/Localization/framework.{id,en}.translations.json` (embedded) | FrogFrame developers |
| Application dictionaries | `*.translations.json` next to the `*.doctype.json` files (loaded by `LoadFrom`) | Application developers |
| Tenant translations | **Settings → System → Translations** (table `frogframe_translations`) | Administrators, per tenant |

A text without a translation is shown as written.

File format:

```json
{
  "language": "en",
  "translations": {
    "Pemohon": "Requester",
    "{0} wajib diisi": "{0} is required"
  }
}
```

## 2. What is translated

- **Metadata**: DocType titles, field labels and descriptions, tabs, sections, Select options (display only), workflow
  states and actions — in the form, list, child tables, sidebar, breadcrumbs and home page.
- **Navigation**: module, menu group and menu names (also those added by administrators, through the Translations page).
- **UI text** of the desk shell, home page, list page, form page (toolbar, tabs, attachments, history, email and
  auto-repeat drawers), child tables, link fields, audit timeline and the BlazorBlueprint components (data grid,
  pagination, date picker…).
- **Messages from the server** (validation errors, rule messages, hook messages) through **templates**: an entry whose
  text contains `{0}`, `{1}`… matches a whole message and its parts (usually field labels) are translated too.

Not translated yet: the administration pages (DocType Editor, Server Scripts, Workflows, …), print formats, emails
and notifications, Excel import templates, and relative times ("5m ago"). Number and date **formats** stay Indonesian
in both languages (`1.000,50`, `12 Nov 2026`), so inputs behave the same for everyone.

## 3. Writing translatable UI

```razor
@inject UiText T

<h1>@T.Title(meta)</h1>                              @* DocType title *@
<BbButton>@T["Save draft"]</BbButton>                @* fixed text *@
<p>@T.Format("Back to {0}", T.Title(meta))</p>       @* text with arguments *@
<span>@T.Value(field, value)</span>                  @* formatted value; Select/Check values translated *@
<BbAlertDescription>@T.Message(error)</BbAlertDescription> @* message composed on the server *@
```

- Use `T.Field(f)` / `T.Section(section)` to give a component a **copy** of a compiled field (or a section) with
  translated label and description. The shared metadata is never changed; names, rules and options stay the same.
- Write new UI text in English and add the Indonesian translation to `framework.id.translations.json`.
- Keep placeholders identical in source and translation (`{0}`, `{1}`); the Translations page refuses translations
  that change them.

## 4. Configuration

| Key | Default | Meaning |
|---|---|---|
| `Localization:DefaultLanguage` | `id` | Language for users who have not chosen one |

`app.UseFrogFrameLocalization()` (after `UseAuthentication`) sets the UI culture per request (cookie → saved
preference → default) and maps `GET /ui/language/{code}?returnUrl=…` for the language switch. The format culture is
always `id-ID`.

## 5. Translations page

**Settings → System → Translations** (System Manager / Administrator):

- **Metadata & menus**: every text of the metadata and menus of this tenant, with its translation and where it comes
  from (*Application*, *Custom*, *Missing*). Edit and save per row; an empty translation removes the custom one.
- **Custom translations**: add a translation for any text on screen (buttons, messages).
- **Import / export**: copy the tenant's translations as a `*.translations.json` file (to ship them with the
  application) or paste one to import it.

Tenant translations are cached; other application instances see changes within 5 minutes.
