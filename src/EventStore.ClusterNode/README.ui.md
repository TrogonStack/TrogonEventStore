# Cluster Node UI Assets

The generated Tailwind stylesheet is committed so .NET builds do not require Node.js.

Run the CSS build whenever Tailwind utility classes change in:

- `styles/app.css`
- `ui-assets/js/*.js`
- `Components/**/*.razor`

```sh
npm ci
npm run build:css
```

Commit the updated `ui-assets/css/tailwind.generated.css` with the source change.
