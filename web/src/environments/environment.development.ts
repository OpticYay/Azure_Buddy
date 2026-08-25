// Swapped in for environment.ts at build time via the "fileReplacements" entry in angular.json.
//
// IMPORTANT: confirm this matches the port your API is actually running on. This project's
// src/AzureBuddy.Api/Properties/launchSettings.json defines an "http" profile on port 5013 - if you
// start the API a different way (e.g. plain `dotnet run` with no launch profile), the port may differ
// (ASP.NET Core's own default is 5000 when no launchSettings profile applies), so check your API's
// console output ("Now listening on: ...") and update this value to match.
export const environment = {
  apiUrl: 'http://localhost:5013',
};
