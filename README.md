# Azure_Buddy

Chatbot that seamlessly integrates with Azure DevOps and helps manage Work Items and CRUDs related to them.

## What's in this repo

- [`N8N chatbot files/QA Azure Buddy (Jul 26 at 23_30_31).json`](N8N%20chatbot%20files/QA%20Azure%20Buddy%20%28Jul%2026%20at%2023_30_31%29.json) — an [n8n](https://n8n.io/) workflow export implementing the chatbot. It exposes a chat trigger backed by an AI agent (Google Gemini, with a local Ollama model as fallback) that can create, search, update, and link Azure DevOps work items via the Azure DevOps REST API.

## Setup

1. Import the workflow JSON into your n8n instance (Workflows → Import from File).
2. Configure the following credentials in n8n:
   - **Azure DevOps** (HTTP Basic Auth, used by the `httpRequest` nodes) — a Personal Access Token (PAT) with Work Items read/write scope.
   - **Google Gemini (PaLM) API** — for the primary chat model.
   - **Ollama** — optional, used as a local fallback chat model.
3. Set the following environment variables on the n8n instance:
   - `ADO_ORG` — your Azure DevOps organization name.
   - `ADO_PROJECT` — your Azure DevOps project name.
   - `ADO_API_VERSION` — Azure DevOps REST API version (e.g. `7.1`).
4. Activate the workflow and send a message to the chat trigger to interact with the bot.

## Notes

- No secrets are stored in the workflow export; credentials are referenced by n8n credential ID and must be configured separately in your own n8n instance.
