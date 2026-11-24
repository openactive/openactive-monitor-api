#!/usr/bin/env node
// PreToolUse guard for the `bigquery` MCP server (.mcp.json).
//
// - Schema lookups (get_table_info, list_table_ids, ...) are redirected to docs/bigquery-schema.md.
// - execute_sql is allowed only for a single SELECT (or WITH ... SELECT) statement.
// - Every other bigquery tool is blocked, including ones a future server version adds.
//
// Exit 2 blocks the call and shows stderr to Claude. This script fails closed: any unexpected
// error also exits 2, because a crashing hook (exit 1) would let the call through.
//
// The server itself also runs with BIGQUERY_WRITE_MODE=blocked, and the service account is
// read-only. This hook is the first of those three layers, not the only one.

import { readFileSync } from "node:fs";

const SCHEMA_TOOLS = new Set([
	"get_table_info",
	"list_table_ids",
	"get_dataset_info",
	"list_dataset_ids",
	"search_catalog",
]);

// Statements or clauses that can change data, schema, permissions or session state.
const FORBIDDEN = [
	"INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE",
	"CREATE", "DROP", "ALTER", "UNDROP", "RENAME",
	"GRANT", "REVOKE",
	"EXPORT", "LOAD", "COPY", "CLONE", "SNAPSHOT",
	"CALL", "EXECUTE", "DECLARE", "SET", "BEGIN", "COMMIT", "ROLLBACK", "TRANSACTION",
	"ASSERT", "RAISE",
];

function block(message) {
	process.stderr.write(`${message}\n`);
	process.exit(2);
}

// Replaces string literals, quoted identifiers and comments with spaces, so keywords inside
// them ('DELETE', `my.update_table`, -- drop this) aren't mistaken for statements.
function stripLiteralsAndComments(sql) {
	let out = "";
	let i = 0;
	while (i < sql.length) {
		const c = sql[i];
		const next = sql[i + 1];

		if (c === "-" && next === "-" || c === "#") {
			while (i < sql.length && sql[i] !== "\n") i++;
			out += " ";
			continue;
		}
		if (c === "/" && next === "*") {
			const end = sql.indexOf("*/", i + 2);
			if (end < 0) block("BigQuery guard: unterminated /* comment in SQL.");
			i = end + 2;
			out += " ";
			continue;
		}
		if (c === "'" || c === '"' || c === "`") {
			// Raw prefix (r'...', rb"...") turns off backslash escapes.
			const raw = c !== "`" && /(^|[^A-Za-z0-9_])(r|rb|br)$/i.test(out);
			const triple = c !== "`" && sql.startsWith(c.repeat(3), i);
			const quote = triple ? c.repeat(3) : c;
			i += quote.length;
			let closed = false;
			while (i < sql.length) {
				if (!raw && sql[i] === "\\") { i += 2; continue; }
				if (sql.startsWith(quote, i)) { i += quote.length; closed = true; break; }
				i++;
			}
			if (!closed) block("BigQuery guard: unterminated quoted string or identifier in SQL.");
			out += " ";
			continue;
		}
		out += c;
		i++;
	}
	return out;
}

function checkSql(sql) {
	if (typeof sql !== "string" || !sql.trim()) block("BigQuery guard: execute_sql was called without SQL.");

	const code = stripLiteralsAndComments(sql).replace(/[\s;]+$/, "");

	if (code.includes(";")) {
		block("BigQuery guard: only a single statement is allowed. Remove the extra statements (`;`).");
	}

	const first = code.replace(/^[\s(]+/, "").match(/^[A-Za-z_]+/)?.[0]?.toUpperCase();
	if (first !== "SELECT" && first !== "WITH") {
		block(`BigQuery guard: only SELECT queries are allowed against BigQuery; this statement starts with ${first ?? "nothing recognisable"}.`);
	}

	const hit = FORBIDDEN.find((kw) => new RegExp(`\\b${kw}\\b`, "i").test(code));
	if (hit) {
		block(`BigQuery guard: \`${hit}\` is not allowed. BigQuery access from this repo is read-only SELECT only. If \`${hit}\` is a column name, wrap it in backticks.`);
	}
}

try {
	const input = JSON.parse(readFileSync(0, "utf8"));
	const tool = String(input.tool_name ?? "").replace(/^mcp__bigquery__/, "");

	if (SCHEMA_TOOLS.has(tool)) {
		block(
			"Schema lookups are served from docs/bigquery-schema.md. Read that file instead of calling "
			+ `${tool}. If it is missing a table or column, or BigQuery rejects a column it lists, the file is `
			+ "stale: regenerate it with the INFORMATION_SCHEMA queries at its end (via execute_sql) and update it.",
		);
	}

	if (tool === "execute_sql") {
		checkSql(input.tool_input?.sql);
		process.exit(0);
	}

	block(`BigQuery guard: the bigquery tool \`${tool}\` is not allowed in this repo. Use execute_sql with a SELECT query.`);
} catch (err) {
	block(`BigQuery guard failed, so the call was blocked: ${err?.message ?? err}`);
}
