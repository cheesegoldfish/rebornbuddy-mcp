#!/usr/bin/env node
// RbMcp MCP shim.
//
// Translates MCP stdio JSON-RPC into the plugin's loopback HTTP API. Lives outside the
// RebornBuddy process on purpose: the MCP spec moves, and nothing about it should ever
// require restarting the game client to pick up. The plugin's HTTP surface is three
// stable routes; everything protocol-shaped happens here.
//
// Zero dependencies - MCP stdio framing is newline-delimited JSON-RPC 2.0, and Node has
// had fetch built in since 18.
//
// Usage:  node rb-mcp.mjs            (defaults to 127.0.0.1:8787)
//         RBMCP_PORT=9000 node rb-mcp.mjs
//
// Auth: the plugin writes a random token to RbMcp.token beside its DLL. Point
// RBMCP_TOKEN_FILE at it (deploy.ps1 does this for you) or set RBMCP_TOKEN directly.

import { createInterface } from 'node:readline';
import { readFileSync } from 'node:fs';

const HOST = process.env.RBMCP_HOST ?? '127.0.0.1';
const PORT = process.env.RBMCP_PORT ?? '8787';
const BASE = `http://${HOST}:${PORT}`;
const HTTP_TIMEOUT_MS = Number(process.env.RBMCP_TIMEOUT_MS ?? 30000);
const TOKEN_FILE = process.env.RBMCP_TOKEN_FILE;

const SERVER_INFO = { name: 'rbmcp', version: '0.1.0' };
const FALLBACK_PROTOCOL = '2025-06-18';

// Cached rather than read per request, but re-read on a 401 - the plugin rotates the token
// if the file is ever damaged, and a stale shim would otherwise need a restart.
let cachedToken = null;

function readToken({ refresh = false } = {}) {
  if (cachedToken && !refresh) return cachedToken;

  if (process.env.RBMCP_TOKEN) {
    cachedToken = process.env.RBMCP_TOKEN.trim();
    return cachedToken;
  }

  if (TOKEN_FILE) {
    try {
      cachedToken = readFileSync(TOKEN_FILE, 'utf8').trim();
      return cachedToken;
    } catch (err) {
      log(`could not read token file ${TOKEN_FILE}: ${err.message}`);
    }
  }

  cachedToken = null;
  return null;
}

function authHeaders() {
  const token = readToken();
  return token ? { authorization: `Bearer ${token}` } : {};
}

// stdout is the transport; anything diagnostic has to go to stderr or it corrupts the stream.
const log = (...args) => console.error('[rb-mcp]', ...args);

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

function reply(id, result) {
  send({ jsonrpc: '2.0', id, result });
}

function replyError(id, code, message) {
  send({ jsonrpc: '2.0', id, error: { code, message } });
}

async function callBridge(path, init = {}, { isRetry = false } = {}) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), HTTP_TIMEOUT_MS);

  try {
    const response = await fetch(`${BASE}${path}`, {
      ...init,
      headers: { ...authHeaders(), ...(init.headers ?? {}) },
      signal: controller.signal
    });
    const text = await response.text();

    // A rotated token looks exactly like a misconfigured one. Re-read the file once and
    // retry before reporting failure, so the common case self-heals.
    if (response.status === 401 && !isRetry) {
      clearTimeout(timer);
      const before = cachedToken;
      if (readToken({ refresh: true }) && cachedToken !== before) {
        return callBridge(path, init, { isRetry: true });
      }

      return {
        ok: false,
        status: 401,
        body: {
          error:
            `RbMcp rejected the auth token. Set RBMCP_TOKEN_FILE to the RbMcp.token file ` +
            `beside the plugin DLL (RebornBuddy\\Plugins\\RbMcp\\RbMcp.token), or set ` +
            `RBMCP_TOKEN directly. Re-running scripts/deploy.ps1 wires this up.`
        }
      };
    }

    try {
      return { ok: response.ok, status: response.status, body: JSON.parse(text) };
    } catch {
      return { ok: false, status: response.status, body: { error: text || '(empty response)' } };
    }
  } catch (err) {
    // The overwhelmingly common cause is "RebornBuddy isn't running", so say that rather
    // than leaking ECONNREFUSED to the model.
    const reason =
      err.name === 'AbortError'
        ? `timed out after ${HTTP_TIMEOUT_MS}ms`
        : `${err.cause?.code ?? err.message}`;

    return {
      ok: false,
      status: 0,
      body: {
        error:
          `Cannot reach RbMcp at ${BASE} (${reason}). ` +
          `Check that RebornBuddy is running and the RbMcp plugin is enabled.`
      }
    };
  } finally {
    clearTimeout(timer);
  }
}

async function handleInitialize(id, params) {
  // Echo the client's protocol version when it names one; guessing lower than the client
  // is the failure mode that actually breaks handshakes.
  const protocolVersion = params?.protocolVersion ?? FALLBACK_PROTOCOL;

  reply(id, {
    protocolVersion,
    capabilities: { tools: { listChanged: false } },
    serverInfo: SERVER_INFO
  });
}

async function handleToolsList(id) {
  const res = await callBridge('/tools');

  if (!res.ok) {
    // Advertise no tools rather than failing the handshake - the client stays usable and
    // the user gets a readable error the first time they call something.
    log('tools/list failed:', res.body?.error);
    reply(id, { tools: [] });
    return;
  }

  reply(id, { tools: res.body.tools ?? [] });
}

async function handleToolsCall(id, params) {
  const name = params?.name;
  const args = params?.arguments ?? {};

  if (!name) {
    replyError(id, -32602, 'Missing tool name.');
    return;
  }

  const res = await callBridge('/rpc', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ tool: name, args })
  });

  const payload = res.ok && res.body?.ok ? res.body.result : res.body;
  const isError = !res.ok || res.body?.ok === false;

  reply(id, {
    content: [{ type: 'text', text: JSON.stringify(payload, null, 2) }],
    isError
  });
}

async function dispatch(message) {
  const { id, method, params } = message;

  // Notifications carry no id and must not be answered.
  if (id === undefined || id === null) return;

  switch (method) {
    case 'initialize':
      return handleInitialize(id, params);
    case 'tools/list':
      return handleToolsList(id);
    case 'tools/call':
      return handleToolsCall(id, params);
    case 'ping':
      return reply(id, {});
    case 'resources/list':
      return reply(id, { resources: [] });
    case 'prompts/list':
      return reply(id, { prompts: [] });
    default:
      return replyError(id, -32601, `Method not found: ${method}`);
  }
}

const rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

rl.on('line', async (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;

  let message;
  try {
    message = JSON.parse(trimmed);
  } catch (err) {
    log('dropping unparseable line:', err.message);
    return;
  }

  try {
    await dispatch(message);
  } catch (err) {
    log('dispatch failed:', err);
    if (message?.id !== undefined && message?.id !== null) {
      replyError(message.id, -32603, `Internal error: ${err.message}`);
    }
  }
});

rl.on('close', () => process.exit(0));
