# Fixed POC HTML Style Guide

Apply this fixed style and document structure to every generated `poc-demo.html`.
If the AI Design Spec requests a different business domain, keep this shell/layout and only change menu labels, content, data, and actions.

## Output constraints
- Generate exactly one self-contained HTML file.
- Use inline `<style>` and inline `<script>` only.
- Do not use external CSS, fonts, images, CDN, package managers, or backend calls.
- Keep the file runnable by opening it directly in a browser.
- Use semantic, readable HTML with stable class names.

## Mandatory page structure
The HTML must follow this order:
1. `<!DOCTYPE html>` and `<html lang="en">`.
2. `<head>` with UTF-8 charset, viewport meta, title, and one inline `<style>` block.
3. `<body>` containing:
   - `.brand-strip`: fixed-height top multicolor corporate strip.
   - `.app-shell`: full-height two-column layout.
   - `aside.sidebar`: dark left navigation.
   - `main.main`: content area with `.topbar` and `.content`.
   - Optional `.modal-backdrop` for create/edit/details demo.
4. One inline `<script>` block at the end of body.

## Visual identity
- Overall style: enterprise dashboard, clean, professional, similar to the provided Web Core Demo reference.
- Top brand strip: 6px height, segmented corporate colors: red, purple, navy, blue, cyan, green.
- Sidebar:
  - Width: 316px on desktop.
  - Background: `#2b2f33`.
  - Text: white.
  - Active item background: `#0096d6`.
  - Header area: app name in bold, close icon on the right.
  - Navigation items: icon box + label + optional chevron.
- Topbar:
  - Height: 54px.
  - White background, thin bottom border.
  - Left: current page title/breadcrumb.
  - Right: simple circular brand mark and bold brand text from the product/company name when available.
- Content:
  - Padding: 40px 32px.
  - White background.
  - Main heading: 32px bold.
  - Intro paragraph: max-width about 1450px.
  - Dashboard cards use light gray background `#eef0f2`, 6px blue top border `#008ecf`, no rounded corners unless functionally needed.

## Required reusable components
Use these component patterns where relevant:
- `.overview-grid`: responsive CSS grid using `repeat(auto-fit, minmax(240px, 1fr))`.
- `.feature-card`: light gray card with blue top border, title, description, optional metrics/actions.
- `.toolbar`: search/filter/action row above lists.
- `.data-table`: full-width table with compact rows, sticky-like header styling, status badges.
- `.status-badge`: variants `success`, `warning`, `danger`, `info`, `neutral`.
- `.btn`: variants `primary`, `secondary`, `ghost`, `danger`.
- `.form-grid`, `.field`, `.modal-card` for create/edit/details fake flows.

## Interaction rules
- Sidebar navigation must switch visible sections without page reload.
- Buttons for create/edit/details must open a mock modal or update mock state using simple JavaScript.
- Search/filter controls should filter mock rows when a list/table exists.
- Keep JavaScript small, readable, and deterministic.

## Responsive rules
- Desktop preserves the screenshot-like sidebar + content layout.
- Below 900px, sidebar can become narrower or stack above content, but all sections must remain usable.

## Content rules
- Use the AI Design Spec to decide actual modules/screens.
- Always include an Overview section first.
- Add enough realistic mock data for client demo.
- Do not leave placeholder text such as "Lorem ipsum".
