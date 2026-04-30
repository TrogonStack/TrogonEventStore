# Cluster Node UI Assets

The generated Tailwind stylesheet is committed so .NET builds do not require Node.js.

Run the CSS build whenever Tailwind utility classes change in:

- `styles/app.css`
- `ui-assets/js/*.js`
- `Components/**/*.razor`

```sh
mise run ui:css
```

Commit the updated `ui-assets/css/tailwind.generated.css` with the source change.

Use `mise run ui:css:watch` while iterating locally.
