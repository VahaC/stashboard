# Stashboard — Frontend

React + TypeScript + Vite. Self-hosted status dashboard SPA.

## Requirements

- Node.js 20.19+ or 22.12+
- npm 10+

## Install dependencies

```bash
npm install
```

Run once after cloning or whenever `package.json` changes.

## Commands

### `npm run dev`

Starts the development server with HMR (hot module replacement).  
Available at [http://localhost:5173](http://localhost:5173) by default.

### `npm run build`

Type-checks the project with TypeScript (`tsc -b`), then produces a production bundle in `dist/`.

### `npm run preview`

Serves the contents of `dist/` with a local static server.  
Use this to verify the production build before deploying.

> Run `npm run build` first.

### `npm run lint`

Runs ESLint across all project files and reports errors and warnings.

## Updating dependencies

Check for outdated packages:

```bash
npm outdated
```

Update a specific package to its latest version:

```bash
npm install <package>@latest
```

Update all packages to the latest minor/patch versions allowed by `package.json`:

```bash
npm update
```

## Troubleshooting

### Vite says Node version is unsupported

If you see an error like "Vite requires Node.js version 20.19+ or 22.12+", switch to a compatible Node version and reinstall dependencies.

With nvm-windows:

```powershell
nvm install 20.19.0
nvm use 20.19.0
```

Then reinstall project dependencies:

```powershell
Remove-Item -Recurse -Force .\node_modules
npm install
```

### Rolldown "Cannot find native binding"

This is typically caused by optional dependency install issues with npm.

```powershell
Remove-Item -Recurse -Force .\node_modules
Remove-Item -Force .\package-lock.json
npm install
```

## Stack

| Library | Purpose |
|---|---|
| React 19 | UI |
| React Router 7 | Client-side routing |
| TanStack Query 5 | Server state & caching |
| Zustand | Client state (session, theme) |
| Axios | HTTP client |
| React Hook Form + Zod | Forms & validation |
| Tailwind CSS 4 | Styling |
| shadcn/ui (Radix) | UI components |
| Vite 8 | Build tool & dev server |
