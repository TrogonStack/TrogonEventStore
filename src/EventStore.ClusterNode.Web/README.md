# Cluster Node Web Assets

The generated Tailwind stylesheet is committed so .NET builds do not require Node.js.

Run the CSS build whenever Tailwind utility classes change in:

- `styles/app.css`
- `clusternode-web/index.html`
- `../EventStore.ClusterNode/Components/**/*.razor`

```sh
npm ci
npm run build:css
```

Commit the updated `clusternode-web/css/tailwind.generated.css` with the source change.
