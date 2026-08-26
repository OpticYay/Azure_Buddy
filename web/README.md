# Web

The Angular frontend for the AzureBuddy QA Chatbot API - a standalone-component Angular app (no NgModules) covering auth, per-user ADO settings, and the chat interface.

This project was generated using [Angular CLI](https://github.com/angular/angular-cli) version 22.1.2.

## Environment setup

Two things need to line up before this app can talk to the backend:

1. **The API must be running and CORS-configured for this app's origin.** From the repo root:

   ```bash
   dotnet run --project src/AzureBuddy.Api --launch-profile http
   ```

   Note the port it prints ("Now listening on: ..."). The `http` launch profile in `src/AzureBuddy.Api/Properties/launchSettings.json` uses port 5013; running `dotnet run` without a profile falls back to ASP.NET Core's own default (5000), so always check the actual console output rather than assuming.
   The API's `Program.cs` allows CORS requests from `http://localhost:4200` by default (see the `Cors:AllowedOrigins` config section) - if you serve this app from a different port/host, add it there.

2. **This app's API base URL must match that port.** It's set in `src/environments/environment.development.ts` (used automatically by `ng serve`) - update the `apiUrl` value if your API isn't on port 5013. `src/environments/environment.ts` is the equivalent file for production builds (`ng build`) - point it at wherever the API is actually deployed before shipping.

## Development server

To start a local development server, run:

```bash
ng serve
```

Once the server is running, open your browser and navigate to `http://localhost:4200/`. The application will automatically reload whenever you modify any of the source files.

You'll land on `/login` - register an account, then set up your Azure DevOps connection at `/settings/ado` before the chat features that talk to ADO (creating/viewing work items, attaching screenshots) will work end-to-end.

## Code scaffolding

Angular CLI includes powerful code scaffolding tools. To generate a new component, run:

```bash
ng generate component component-name
```

For a complete list of available schematics (such as `components`, `directives`, or `pipes`), run:

```bash
ng generate --help
```

## Building

To build the project run:

```bash
ng build
```

This will compile your project and store the build artifacts in the `dist/` directory. By default, the production build optimizes your application for performance and speed.

## Running unit tests

To execute unit tests with the [Vitest](https://vitest.dev/) test runner, use the following command:

```bash
ng test
```

## Running end-to-end tests

For end-to-end (e2e) testing, run:

```bash
ng e2e
```

Angular CLI does not come with an end-to-end testing framework by default. You can choose one that suits your needs.

## Additional Resources

For more information on using the Angular CLI, including detailed command references, visit the [Angular CLI Overview and Command Reference](https://angular.dev/tools/cli) page.
