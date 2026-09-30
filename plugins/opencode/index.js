// terminalfs for opencode: a tree for each session, and the session's own permission rules
// applied to every command run through it.
//
// opencode checks a write to <tree>/ctl/<name> as an edit of a file, and its shell rules never see
// the command inside. This plugin hooks that check, asks `terminalfs hook opencode check` what the
// session's rules say about the command, and sets the check's effect to the answer: deny refuses
// the write with the reason, ask shows opencode's own prompt — the write's diff, which is the
// command — and allow lets it through. The built-in read and write are also made callable from
// opencode's execute tool, so a script can write commands and read what they did in one turn; a
// write made on its own gets the command's state and output added to its result instead.
//
// Everything that decides is in the terminalfs binary, shared with the Claude Code plugin; this
// file only carries opencode's events to it. The skill comes from the binary too, so it is the one
// written for the terminalfs that runs. terminalfs has to be on the PATH, and
// `terminalfs plugin install opencode` puts the plugin that goes with it where opencode finds it.

import { spawn } from "node:child_process"
import { readFile } from "node:fs/promises"
import { fileURLToPath } from "node:url"

// Where opencode says the skill is from. There is no such file, and it is not called SKILL.md, so
// opencode does not list this plugin's own files as the skill's.
const skillPath = fileURLToPath(new URL("./terminalfs.md", import.meta.url))

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


/** The text a tool result carries, with `more` added to it. */
function appended(content, more) {
  if (content === undefined || typeof content === "string") return `${content ?? ""}\n\n${more}`
  return [...content, { type: "text", text: more }]
}

export default {
  id: "terminalfs",
  setup: async (ctx) => {
    const directory = ctx.location.directory
    /** sessionID -> Promise<{ mount?, context, root? }> */
    const trees = new Map()
    /** tool call id -> [{ tool, input }] for every call under way. Calls made from execute share its id. */
    const calls = new Map()
    /** tool call id -> refusals given to calls under it, for an execute script to be told of. */
    const refused = new Map()
    /** Where session trees live, once a session has started and said. */
    let root
    const mentionsTrees = (value) => JSON.stringify(value ?? "").includes(root ?? "/terminalfs")

    const start = (sessionID) => {
      if (!trees.has(sessionID)) {
        trees.set(
          sessionID,
          terminalfs("session-start", { session_id: sessionID, cwd: directory })
            .then((tree) => {
              root ??= tree.root
              return tree
            })
            .catch((error) => ({
              context: `terminalfs could not start a tree for this session, so the terminalfs skill cannot be used: ${error.message}`,
            })),
        )
      }
      return trees.get(sessionID)
    }

    const stop = async (sessionID) => {
      const starting = trees.get(sessionID)
      if (!starting) return
      trees.delete(sessionID)
      // A start still under way would otherwise mount the tree after it had been stopped.
      await starting
      await terminalfs("session-end", { session_id: sessionID }).catch(() => {})
    }

    /** The session's permission rules, in the order opencode resolves them: agent, then session. */
    const rules = async (sessionID, agentID) => {
      const session = await ctx.session.get({ sessionID })
      const agent = (await ctx.agent.get({ agentID: agentID ?? session?.agent }))?.data
      return [...(agent?.permissions ?? []), ...(session?.permissions ?? [])]
    }

    // opencode's code mode leaves the built-in file tools out, and a command written and read back
    // from one execute script is what makes it cost one turn. Each named built-in is copied under
    // file_<name> for code mode alone: the copy shares the built-in's own execute, so its
    // permission checks — this plugin's included — are the built-in's, and the built-in itself
    // stays an ordinary tool. `codemode` in this plugin's options names others; [] turns it off.
    const codemode = Array.isArray(ctx.options?.codemode) ? ctx.options.codemode : ["read", "write"]
    await ctx.tool.transform((editor) => {
      for (const id of codemode) {
        const tool = editor.get(id)
        if (!tool || editor.get(`file_${id}`)) continue
        const { id: _, ...info } = tool
        // Named after the built-in's permission, so a rule that switches the built-in off switches
        // its copy off too.
        editor.add({
          ...info,
          name: `file_${id}`,
          options: { ...info.options, permission: info.options?.permission ?? id, codemode: true, pinned: true },
        })
      }
    })

    // Without the binary there is no skill to add; each session's context then says why there is
    // no tree, rather than opencode failing to start over a plugin.
    const skill = await terminalfs("skill", {}).then(({ content }) => content, () => undefined)
    if (skill) {
      await ctx.skill.transform((editor) => {
        editor.add({
          id: "terminalfs",
          name: "terminalfs",
          description:
            "Run shell commands through this session's terminalfs tree: write a command and read what it did in one execute script.",
          path: skillPath,
          content: skill,
        })
      })
    }

    // Awaited before a prompt is handled, so the tree is there before the agent can use it.
    await ctx.session.hook("prompt", async (event) => {
      await start(event.sessionID)
    })

    await ctx.session.hook("context", async (event) => {
      const tree = await trees.get(event.sessionID)
      if (tree?.context) event.system.push({ type: "text", text: tree.context })
    })

    // Every call is kept until it is done, whatever its tool or its input: a path can be spelled
    // without naming the tree (~/.., a relative one), a built-in can be put under another name, and
    // the permission check that follows is judged on where opencode resolved the file to, with the
    // content from here. A script run by execute makes its calls under execute's own id, so an id
    // holds a list, and terminalfs picks the call a check is for.
    await ctx.tool.hook("execute.before", (event) => {
      const pending = calls.get(event.id) ?? []
      pending.push({ tool: event.tool, input: event.input })
      calls.set(event.id, pending)
    })

    await ctx.permission.hook("evaluate", async (event) => {
      const pending = event.source?.id ? (calls.get(event.source.id) ?? []) : []
      // A shell command can reach a tree however it spells the path, so every one is checked;
      // anything else only when it names where trees live.
      if (event.action !== "shell" && !mentionsTrees(event.resources) && !mentionsTrees(pending)) return

      // What is sent is what the answer's call index refers to, whatever comes and goes meanwhile.
      const sent = [...pending]
      try {
        const decision = await terminalfs("check", {
          session_id: event.sessionID,
          directory,
          action: event.action,
          resources: event.resources,
          calls: sent,
          rules: await rules(event.sessionID, event.agent),
        })
        if (decision.effect === "none") return
        event.effect = decision.effect
        if (decision.message) event.message = decision.message
        // A call refused here never reaches execute.after, and one left pending would be counted
        // against the next write the script makes.
        if (decision.effect === "deny" && Number.isInteger(decision.call)) {
          const at = pending.indexOf(sent[decision.call])
          if (at >= 0) pending.splice(at, 1)
          if (pending.length === 0) calls.delete(event.source.id)
        }
        // Kept for the script a refused call was made from, which is told when it ends.
        if (decision.effect === "deny" && decision.message && pending.some((call) => call.tool === "execute")) {
          refused.set(event.source.id, [...(refused.get(event.source.id) ?? []), decision.message])
        }
      } catch (error) {
        // A check that fell over is not a way through.
        event.effect = "deny"
        event.message = `terminalfs could not check this call: ${error.message}`
      }
    })

    // A command written to ctl/<name> ran; what it did is added to the write's result, so the
    // agent needs no second call to read it.
    await ctx.tool.hook("execute.after", async (event) => {
      const pending = calls.get(event.id) ?? []
      const at = pending.findIndex((call) => call.tool === event.tool && JSON.stringify(call.input) === JSON.stringify(event.input))
      const call = pending[at]
      if (at >= 0) pending.splice(at, 1)
      if (pending.length === 0) calls.delete(event.id)

      // A call refused inside an execute script reaches the script only as "Unable to write", so
      // the reasons are added to the script's own result when it ends. Whatever the script left
      // pending — a call the user declined, one that failed — ends with it.
      if (event.tool === "execute") {
        const reasons = refused.get(event.id)
        refused.delete(event.id)
        calls.delete(event.id)
        if (reasons && event.status === "completed") event.result.content = appended(event.result.content, reasons.join("\n"))
        return
      }
      if (!call || event.status !== "completed" || typeof call.input?.content !== "string") return

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
