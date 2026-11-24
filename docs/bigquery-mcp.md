# BigQuery MCP for Claude Code

The repo includes a read-only BigQuery MCP server for Claude Code. It is configured in `.mcp.json`,
and its permissions are in `.claude/settings.json`. The server runs as a shared read-only service
account and never uses your personal Google login.

When you sign in, you get credentials that can only *impersonate* that service account. BigQuery
sees every query as coming from the service account, so you can never do more than it can, which is
read data and run query jobs in `openactive-monitor`.

For table and column lookups, read [bigquery-schema.md](bigquery-schema.md) first. It's a local
snapshot of the dataset's schema, so you only need the MCP for real data or to check that the
snapshot is still current.

## Guardrails

Three independent layers keep the MCP to read-only SELECT queries. Any one of them is enough to stop
a write.

| Layer | Where | What it does |
|---|---|---|
| Claude Code hook | `.claude/hooks/bigquery-guard.mjs`, wired up in `.claude/settings.json` | Runs before every `bigquery` tool call. `execute_sql` is allowed only for a single `SELECT` or `WITH … SELECT` statement with no DML/DDL/scripting keywords outside strings and comments. Schema tools (`get_table_info`, `list_table_ids`, `get_dataset_info`, `list_dataset_ids`, `search_catalog`) are blocked, with a message pointing to `bigquery-schema.md`. Every other tool is blocked too, including any a future server version adds. If the hook itself errors, the call is blocked. |
| MCP server | `BIGQUERY_WRITE_MODE=blocked` in `.mcp.json` | The toolbox server rejects any `execute_sql` statement whose type isn't SELECT ("write mode is 'blocked', only SELECT statements are allowed"). |
| IAM | The agent service account | Has read-only BigQuery access, so BigQuery itself refuses writes. |

`.claude/settings.json` also denies `analyze_contribution`, `ask_data_insights` and `forecast`
outright, and prompts before every `execute_sql` call.

The server version is pinned in `.mcp.json` (`@toolbox-sdk/server@1.18.0`) because
`BIGQUERY_WRITE_MODE` depends on it. When you bump the version, check that the setting is still
honoured. Ask the agent to dry-run `DECLARE x INT64 DEFAULT 1; SELECT x` with the hook temporarily
disabled: it must fail with the "write mode is 'blocked'" message.

The hook needs Node.js, which you already installed for `npx`. To check the hook on its own:

```bash
echo '{"tool_name":"mcp__bigquery__execute_sql","tool_input":{"sql":"DELETE FROM t WHERE true"}}' \
  | node .claude/hooks/bigquery-guard.mjs; echo "exit=$?"   # prints the reason, exit=2
```

## Using the MCP on your laptop

1. Ask a project maintainer to give your Google account access to the agent service account (see
   [Adding a user](#adding-a-user)) and to send you the service account's email address.

2. Install the [Google Cloud CLI](https://cloud.google.com/sdk/docs/install),
   [Node.js](https://nodejs.org/) (for `npx`) and Claude Code.

3. Create the agent's credentials. This opens a browser so you can sign in with your own account.
   The saved credentials can only act as the read-only service account, and your normal `gcloud`
   login isn't changed:

   ```bash
   CLOUDSDK_CONFIG="$HOME/.config/gcloud-agent-bq" \
     gcloud auth application-default login \
     --impersonate-service-account=<service-account-email>
   ```

   The credentials are saved under `~/.config/gcloud-agent-bq/`, which is where `.mcp.json` looks
   for them.

4. Open the repo in Claude Code and approve the `bigquery` server when you're asked.

5. Run `/mcp` inside Claude Code and check that `bigquery` shows as connected.

6. Test it with this prompt: `List the datasets in openactive-monitor and show me the schema of one table.`

### Troubleshooting

- **You approved or rejected the server by mistake.** Run `claude mcp reset-project-choices`, then
  restart Claude Code.
- **Step 3 fails with a permission error.** Your access probably hasn't been granted yet or hasn't
  taken effect. IAM changes can take a few minutes to propagate, so wait and retry. If it still
  fails, ask the maintainer to run the check in [Reviewing access](#reviewing-access).
- **On Windows, the path in `.mcp.json` doesn't resolve.** If `${HOME}` isn't set, set a `HOME`
  environment variable.
- **The server worked before but now fails to authenticate.** Run step 3 again to refresh the
  credentials.

## Adding a user

*For maintainers.* A user needs one thing: the **Service Account Token Creator** role
(`roles/iam.serviceAccountTokenCreator`) on the agent service account. Grant it on the service
account itself, **not on the project**. If you grant it at project level, the user can impersonate
every service account in `openactive-monitor`, including ones with write access.

You need permission to change the service account's IAM policy (`iam.serviceAccounts.setIamPolicy`),
which the project Owner or Service Account Admin role gives you. The user doesn't need any role on
the project or on BigQuery. Those belong to the service account.

### In the Cloud Console

1. Go to **IAM & Admin → Service Accounts** in project `openactive-monitor`.
2. Open the agent service account, the read-only BigQuery MCP one.
3. Go to the **Principals with access** tab and click **Grant access**.
4. Enter the user's Google account email in **New principals**.
5. Choose the role **Service Accounts → Service Account Token Creator**, then click **Save**.
6. Send the user the service account's email address (shown at the top of the page) and a link to
   [Using the MCP on your laptop](#using-the-mcp-on-your-laptop).

### With gcloud

Look up the service account's email:

```bash
gcloud iam service-accounts list --project=openactive-monitor
```

Grant the role to the user on that service account only:

```bash
gcloud iam service-accounts add-iam-policy-binding <service-account-email> \
  --project=openactive-monitor \
  --member="user:<their-google-email>" \
  --role="roles/iam.serviceAccountTokenCreator"
```

Then send them the service account email.

### Using a group instead

If you expect to onboard several people, grant the role once to a Google Group with
`--member="group:<group-email>"`. After that, adding or removing someone only means changing the
group's membership, and the service account's IAM policy doesn't change.

## Removing a user

Remove the same binding:

```bash
gcloud iam service-accounts remove-iam-policy-binding <service-account-email> \
  --project=openactive-monitor \
  --member="user:<their-google-email>" \
  --role="roles/iam.serviceAccountTokenCreator"
```

In the Console, go to **Principals with access** on the service account and remove the role from
the user. After removal they can't mint new tokens. A token they already hold stays valid until it
expires, which takes up to an hour.

## Reviewing access

List everyone who can currently use the MCP:

```bash
gcloud iam service-accounts get-iam-policy <service-account-email> \
  --project=openactive-monitor
```

Look for members under `roles/iam.serviceAccountTokenCreator`. Also make sure that nobody holds that
role at project level (`gcloud projects get-iam-policy openactive-monitor`), because that would
bypass this per-account control.
