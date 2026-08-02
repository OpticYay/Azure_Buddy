// Used for production builds (`ng build`). Point this at wherever the API is actually deployed
// before shipping - it's a plain object, not a secret, since it ends up in the built JS bundle
// anyway (anyone can view-source it) - never put real secrets (API keys, PATs) in an environment file.
export const environment = {
  apiUrl: 'https://your-deployed-api.example.com',
};
