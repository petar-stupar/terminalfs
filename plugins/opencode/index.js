// terminalfs for opencode: a tree for each session, and the session's own permission rules
// applied to every command run through it.
//
// opencode checks a write to <tree>/ctl/<name> as an edit of a file, and its shell rules never see
// the command inside. This plugin hooks that check, asks `terminalfs hook opencode check` what the
// session's rules say about the command, and sets the check's effect to the answer: deny refuses
// the write with the reason, ask shows opencode's own prompt — the write's diff, which is the
// command — and allow lets it through. Once an allowed write lands, the command's state and output
// are added to the write's result, so a command costs one tool call.
//
// Everything that decides is in the terminalfs binary, shared with the Claude Code plugin; this
// file only carries opencode's events to it. terminalfs has to be on the PATH.

import { spawn } from "node:child_process"
import { readFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"

const skillPath = fileURLToPath(new URL("./SKILL.md", import.meta.url))

/** Runs `terminalfs hook opencode <event>` with `input` as JSON, and returns the JSON it prints. */
function terminalfs(event, input) {
  return new Promise((resolve, reject) => {
    const child = spawn("terminalfs", ["hook", "opencode", event], { stdio: ["pipe", "pipe", "pipe"] })
    let stdout = ""
    let stderr = ""

    child.stdout.on("data", (chunk) => (stdout += chunk))
    child.stderr.on("data", (chunk) => (stderr += chunk))
    child.on("error", reject)
    child.on("close", (code) => {
      if (code !== 0) return reject(new Error(stderr.trim() || `terminalfs hook opencode ${event} exited ${code}`))
      try {
        resolve(stdout.trim() ? JSON.parse(stdout) : {})
      } catch (error) {
        reject(error)
      }
    })

    child.stdin.end(JSON.stringify(input))
  })
}

const mentionsTrees = (value) => JSON.stringify(value ?? "").includes("/terminalfs")

/** The text a tool result carries, with `more` added to it. */
function appended(content, more) {
  if (content === undefined || typeof content === "string") return `${content ?? ""}\n\n${more}`
  return [...content, { type: "text", text: more }]
}

export default {
  id: "terminalfs",
  setup: async (ctx) => {
    const directory = ctx.location.directory
    /** sessionID -> Promise<{ mount?, context }> */
    const trees = new Map()
    /** tool call id -> { tool, input } for calls that name a tree */
    const calls = new Map()

    const start = (sessionID) => {
      if (!trees.has(sessionID)) {
        trees.set(
          sessionID,
          terminalfs("session-start", { session_id: sessionID, cwd: directory }).catch((error) => ({
            context: `terminalfs could not start a tree for this session, so the terminalfs skill cannot be used: ${error.message}`,
          })),
        )
      }
      return trees.get(sessionID)
    }

    const stop = (sessionID) => {
      if (!trees.delete(sessionID)) return Promise.resolve()
      return terminalfs("session-end", { session_id: sessionID }).catch(() => {})
    }

    /** The session's permission rules, in the order its agent resolves them. */
    const rules = async (sessionID, agentID) => {
      const session = (await ctx.session.get({ sessionID }))?.data
      const agent = (await ctx.agent.get({ agentID: agentID ?? session?.agent }))?.data
      return [...(agent?.permissions ?? []), ...(session?.permissions ?? [])]
    }

    const skill = await readFile(skillPath, "utf8")
    await ctx.skill.transform((editor) => {
      editor.add({
        id: "terminalfs",
        name: "terminalfs",
        description:
          "Run shell commands by writing them to this session's terminalfs tree with the write tool, one call per command.",
        path: skillPath,
        content: skill,
      })
    })

    // Awaited before a prompt is handled, so the tree is there before the agent can use it.
    await ctx.session.hook("prompt", async (event) => {
      await start(event.sessionID)
    })

    await ctx.session.hook("context", async (event) => {
      const tree = await trees.get(event.sessionID)
      if (tree?.context) event.system.push({ type: "text", text: tree.context })
    })

    await ctx.tool.hook("execute.before", (event) => {
      if (mentionsTrees(event.input)) calls.set(event.id, { tool: event.tool, input: event.input })
    })

    await ctx.permission.hook("evaluate", async (event) => {
      const call = event.source?.id ? calls.get(event.source.id) : undefined
      if (!call && !mentionsTrees(event.resources)) return

      try {
        const decision = await terminalfs("check", {
          session_id: event.sessionID,
          directory,
          action: event.action,
          resources: event.resources,
          tool: call?.tool,
          input: call?.input,
          rules: await rules(event.sessionID, event.agent),
        })
        if (decision.effect === "none") return
        event.effect = decision.effect
        if (decision.message) event.message = decision.message
      } catch (error) {
        // A check that fell over is not a way through.
        event.effect = "deny"
        event.message = `terminalfs could not check this call: ${error.message}`
      }
    })

    // A command written to ctl/<name> ran; what it did is added to the write's result, so the
    // agent needs no second call to read it.
    await ctx.tool.hook("execute.after", async (event) => {
      const call = calls.get(event.id)
      calls.delete(event.id)
      if (!call || event.status !== "completed" || event.tool !== "write") return

      const tree = await trees.get(event.sessionID)
      const path = String(call.input?.path ?? "")
      const control = tree?.mount ? `${tree.mount}/ctl/` : undefined
      if (!control || !path.startsWith(control)) return

      const name = path.slice(control.length)
      const read = (file) => readFile(`${tree.mount}/cmd/${name}/${file}`, "utf8").catch(() => undefined)
      const state = (await read("wait"))?.trim()
      const exit = (await read("exitcode"))?.trim()
      const stdout = (await read("stdout")) ?? ""

      event.result.content = appended(
        event.result.content,
        state === "running"
          ? `terminalfs: ${name} is still running. Read ${tree.mount}/cmd/${name}/wait again, then stdout.\n\n${stdout}`
          : `terminalfs: ${name} ${state ?? "did not start"}${exit ? `, exit code ${exit}` : ""}\n\n${stdout}`,
      )
    })

    const events = new AbortController()
    ;(async () => {
      for await (const event of ctx.event.subscribe({ signal: events.signal })) {
        const sessionID = event?.data?.sessionID ?? event?.properties?.sessionID ?? event?.sessionID
        if (event?.type === "session.deleted" && sessionID) await stop(sessionID)
      }
    })().catch(() => {})

    return async () => {
      events.abort()
      await Promise.all([...trees.keys()].map(stop))
    }
  },
}
