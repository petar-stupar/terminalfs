using System.Globalization;
using System.Text;
using TerminalFs.Core.Internal.Render;

namespace TerminalFs.Core.Internal.Nodes;

/// <summary>
/// The pages this tree writes about itself.
/// </summary>
internal static class TreeText
{
    /// <summary>The root page: what this tree is and how to drive it.</summary>
    internal static string RootIndex(CommandRegistry registry, DateTimeOffset builtAt)
    {
        var page = new StringBuilder(
            new Frontmatter(OkfType.Of(TerminalNodeKind.Root))
                .Add("title", "Commands, as files")
                .Add("description", "Run a shell command by writing to a file, and read what it did.")
                .Add("shell", registry.Shell.File)
                .Add("directory", registry.WorkingDirectory)
                .AddList("tags", ["shell", "commands", "terminal"])
                .AddTimestamp(builtAt)
                .ToString());

        page.Append(
            """
            # Commands, as files

            Write a command to `/ctl/<name>` and it runs. What it did appears under
            `/cmd/<name>/`, as files you read like any other: `cat` them, read them again as they
            grow, and remove the directory when you are done. You choose the name.

            ## Layout

            ```text
            /ctl/<name>             write a command here to run it, and it appears below
            /cmd/<name>/command      the command, as it was written
            /cmd/<name>/pid          the process, while there is one
            /cmd/<name>/status       running, completed, error or denied
            /cmd/<name>/reason       why it was refused, when it was
            /cmd/<name>/exitcode     once it has stopped
            /cmd/<name>/stdout       what it wrote, as it writes it
            /cmd/<name>/stderr       the same for standard error
            /cmd/<name>/wait         reading this blocks until it stops
            /cmd/<name>/kill         write anything here to end it
            /skills/                how an agent should use this tree, one skill per harness
            ```

            ## Running something

            You choose the name. It is the file you write to and the directory the results appear
            in, so pick one you can recognise later.

            ```text
            echo 'dotnet build' > /ctl/build
            cat /cmd/build/wait
            cat /cmd/build/exitcode
            cat /cmd/build/stdout
            ```

            A command can be several lines. Everything written before the file is closed is the
            command:

            ```text
            cat > /ctl/loop <<'EOF'
            for i in 1 2 3; do
              echo line $i
              sleep 1
            done
            EOF
            ```

            Each command has a file of its own rather than sharing one control file, so several
            callers can start commands at the same time.

            Nothing runs until the file is closed, so a command is never half-executed, and
            `/cmd/<name>/` does not exist until there is a command to describe — a name you take
            and never write to leaves nothing behind. A name runs **once**: once it has run,
            taking it again is refused until its directory is removed.

            You can create the file first and write to it afterwards, and you can write to a
            temporary name and rename it into place. Both work, because a name is only decided by
            the close that has a command in it:

            ```text
            cp /dev/null /ctl/build.tmp        # takes the name
            echo 'dotnet build' > /ctl/build.tmp
            mv /ctl/build.tmp /ctl/build       # it runs as `build`
            ```

            `ls /ctl` shows the names taken but not yet run. `rm /ctl/<name>` gives one back.

            **If the write fails, look under `/cmd/<name>/`.** The error a mount reports is only
            a number — `Operation not permitted`, `File exists` — because that is all the protocol
            underneath it carries. A command refused before it ran is there anyway, with `status`
            reading `denied` and `reason` saying why; it holds those two files and `command`, and
            nothing else, because nothing ran. `File exists` means the name is already a command:
            look at its directory, or pick another name.

            ## While it runs

            `status` says `running`, and `pid` is there. Read `stdout` again and it has grown —
            it is an ordinary file, so `tail -n 20`, `grep` and `wc -l` all work on it.

            `cat /cmd/<name>/wait` blocks until the command stops and then prints its state. It
            gives up after a while and prints `running`, which means the command is still going
            and you may read it again.

            ## Stopping and clearing up

            ```text
            echo x > /cmd/<name>/kill   end it, keeping what it produced
            rm -r /cmd/<name>           remove it, ending it first if it is still running
            ```

            A finished command is removed on its own once nothing has read it for a while, so a
            long session does not fill up with old output. A name taken and never written to is
            freed on the same clock.

            """);

        page.Append("## What is here\n\n");
        page.Append("- [Running a command](ctl/index.md) — how to start one.\n");
        page.Append("- [Commands](cmd/index.md) — ").Append(Count(registry.Commands.Count)).Append(" right now.\n");
        page.Append("- [Skills](skills/index.md) — how an agent should use this tree.\n\n");

        page.Append("## Refusals\n\n");

        if (registry.Options.Settings is { } settings)
        {
            page.Append(
                CultureInfo.InvariantCulture,
                $"""
                Some commands are refused before they run. The rules are read from
                `{settings.Path}`, which is outside this tree and cannot be edited through it;
                {Rules(settings.Current.Count)} in force. A refused command fails the write to
                `/ctl` and then stays: `/cmd/<name>/` holds `command`, `status` — which reads
                `denied` — and `reason`, which names the rule. `rm -r /cmd/<name>` frees the name,
                and it is freed on its own once nothing has read it for a while.

                """);
        }
        else
        {
            page.Append("No rules are loaded, so nothing is refused before it runs.\n");
        }

        return page.ToString();
    }

    /// <summary>The index of <c>/cmd</c>: every command, and where it has got to.</summary>
    internal static string CommandsIndex(CommandRegistry registry)
    {
        IReadOnlyList<Command> commands = registry.Commands;

        var page = new StringBuilder(
            new Frontmatter(OkfType.Of(TerminalNodeKind.Commands))
                .Add("title", "Commands")
                .Add("description", "Every command this server has been asked to run.")
                .Add("count", commands.Count.ToString(CultureInfo.InvariantCulture))
                .AddTimestamp(registry.ChangedAt)
                .ToString());

        page.Append("# Commands\n\n");

        if (commands.Count == 0)
        {
            page.Append(
                """
                Nothing has been run yet. Write a command to `/ctl/<name>` and `<name>` appears
                here — not before, so a name taken and never written to leaves nothing:

                ```text
                echo 'echo hello' > /ctl/first
                ```

                """);

            return page.ToString();
        }

        page.Append("| id | status | exit | command |\n|---|---|---|---|\n");

        foreach (Command command in commands)
        {
            page.Append("| [")
                .Append(command.Id)
                .Append("](")
                .Append(command.Id)
                .Append("/status) | ")
                .Append(command.StatusLine.Trim())
                .Append(" | ")
                .Append(command.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "")
                .Append(" | `")
                .Append(OneLine(command.Text))
                .Append("` |\n");
        }

        page.Append(
            """

            Each directory holds `command`, `status`, `stdout`, `stderr` and `wait`, plus `pid` and
            `kill` while it runs and `exitcode` once it has stopped. One that was refused before it
            ran holds `command`, `status` and `reason`, and nothing else.

            """);

        return page.ToString();
    }

    /// <summary>The index of <c>/ctl</c>: how to run something.</summary>
    internal static string ControlIndex(DateTimeOffset builtAt) =>
        new Frontmatter(OkfType.Of(TerminalNodeKind.Control))
            .Add("title", "Running a command")
            .Add("description", "Write a command to a file named for it.")
            .AddTimestamp(builtAt)
            .ToString()
        + """
          # Running a command

          Write the command to a file named for it. The name is yours to choose: letters, digits,
          `_`, `-` and `.`, up to 64 of them.

          ```text
          echo 'dotnet build' > /ctl/build
          ```

          What it does appears under `/cmd/build/`. Nothing runs until the file is closed, so a
          command is never half-executed, and it may be several lines:

          ```text
          cat > /ctl/tests <<'EOF'
          dotnet test --no-build 2>&1 | tail -40
          EOF
          ```

          A name runs **once**, including one that was itself refused. Taking one that has
          already run is refused until its directory is removed. Between the write and the
          run, reading `/ctl/<name>` gives back the command as written.

          Until a command has been written and the file closed, the name is only taken: nothing
          runs, and `/cmd/<name>/` is not there. So you may create the file and write to it later,
          or write to a temporary name and rename it into place —

          ```text
          echo 'dotnet build' > /ctl/build.tmp
          mv /ctl/build.tmp /ctl/build
          ```

          — and the command runs as `build`, never as `build.tmp`. `ls /ctl` lists the names
          taken and not yet run, and `rm /ctl/<name>` gives one back. A name nobody writes a
          command for is freed on its own after a while.

          Each command has a file of its own rather than sharing one control file, because a
          client merges concurrent writes to a single path: four callers writing at once reached
          this server as one write, and three commands were lost without an error anywhere.

          If a write fails, look under `/cmd/<name>/` — the error a mount reports is only a
          number. A command refused before it ran is there with `status` reading `denied` and
          `reason` saying why. `File exists` means the name is already a command, and its
          directory is why.

          """;

    /// <summary>The skills directory: a skill for each harness.</summary>
    internal static string SkillsIndex(DateTimeOffset builtAt)
    {
        var page = new StringBuilder(
            new Frontmatter(OkfType.Of(TerminalNodeKind.Skill))
                .Add("title", "Skills")
                .Add("description", "How an agent should use this tree, one skill per harness.")
                .AddTimestamp(builtAt)
                .ToString());

        page.Append(
            """
            # Skills

            The cheapest way to run a command depends on the tools a harness has, so each harness
            gets a skill of its own. Every one is called `terminalfs`; load the one for yours.


            """);

        foreach (SkillHarness harness in SkillHarness.All)
        {
            page.Append("- [").Append(harness.Title).Append("](").Append(harness.Directory)
                .Append("/index.md) — `").Append(harness.Directory).Append("/terminalfs/SKILL.md`.\n");
        }

        return page.Append('\n').ToString();
    }

    /// <summary>The directory a harness is pointed at: how it loads the skill in it.</summary>
    internal static string HarnessIndex(SkillHarness harness, DateTimeOffset builtAt) =>
        new Frontmatter(OkfType.Of(TerminalNodeKind.Skill))
            .Add("title", harness.Title)
            .Add("description", $"The terminalfs skill for {harness.Title}, and how it loads it.")
            .AddTimestamp(builtAt)
            .ToString()
        + $"# {harness.Title}\n\n"
        + "- [terminalfs](terminalfs/index.md) — running commands through this filesystem.\n\n"
        + (harness == SkillHarness.OpenCode
            ? """
              The terminalfs plugin for opencode brings this skill with it, starts a tree for each
              session, and checks what runs through it against the session's permission rules.
              Without the plugin, opencode can read skills straight off the mount: add this
              directory — `skills/opencode` under wherever the tree is mounted — to the skills
              paths in its configuration, and nothing needs copying.

              """
            : """
              The terminalfs plugin for Claude Code brings this skill with it, starts a tree for
              each session, and checks what runs through it against the session's permission
              rules. Without the plugin, Claude Code only finds skills under its own
              configuration directory, so copy `terminalfs/SKILL.md` to
              `$CLAUDE_CONFIG_DIR/skills/terminalfs/SKILL.md`
              (`~/.claude/skills/terminalfs/SKILL.md` by default).

              """);

    /// <summary>The page describing a skill, as opposed to the skill itself.</summary>
    internal static string SkillIndex(SkillHarness harness, DateTimeOffset builtAt, string? mountPath) =>
        new Frontmatter(OkfType.Of(TerminalNodeKind.Skill))
            .Add("title", "terminalfs")
            .Add("description", $"Running shell commands by writing to a file, from {harness.Title}.")
            .AddTimestamp(builtAt)
            .ToString()
        + $"# terminalfs\n\n[SKILL.md](SKILL.md) is the skill for {harness.Title}. "
        + (harness == SkillHarness.OpenCode
            ? "opencode can read it where it is;\n[the page above](../index.md) says how.\n\n"
            : "Claude Code needs a copy of it;\n[the page above](../index.md) says where.\n\n")
        + (mountPath, harness == SkillHarness.OpenCode) switch
        {
            (null, true) =>
                """
                The paths in it are written `<mount>`, and it tells opencode how to work out the
                real one: from the session context the terminalfs plugin gives it, or from where it
                read the skill.

                """,
            (null, false) =>
                """
                The paths in it are written `<mount>`. Replace that with where this tree is mounted
                when you copy it, or leave it for the session context to name.

                """,
            (_, true) => "The paths in it are already the ones on this machine.\n",
            (_, false) =>
                """
                The paths in it are already the ones on this machine, so copy it as it is. A copy
                that outlives this tree still gives way to a mount the session context names.

                """,
        };

    /// <summary>
    /// The skill <paramref name="harness"/> reads, with <paramref name="mountPath"/> written into
    /// it where it is known.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No OKF frontmatter: a skill file's frontmatter belongs to the harness, which matches on
    /// <c>name</c> and <c>description</c>, and inventing extra fields there would be noise.
    /// </para>
    /// <para>
    /// These are the only pages that carry the mountpoint, and the only ones written with a
    /// placeholder, for the same reason: they are followed from outside the tree. The other pages
    /// are read through the mount, where <c>/ctl/build</c> is the right way to name a position in
    /// the tree and a reader already standing in it needs no prefix. Do not make them match.
    /// </para>
    /// <para>
    /// Two things are substituted, and nothing else. <c>&lt;where&gt;</c> is the paragraph that
    /// says where the tree is, which depends on the harness and on whether anybody said; it goes
    /// in first, because it may itself say <c>&lt;mount&gt;</c>. Then <c>&lt;mount&gt;</c>, when
    /// the path is known. <c>&lt;name&gt;</c> and <c>&lt;id&gt;</c> in this text are placeholders
    /// a reader is meant to fill in themselves, and a general template pass would eat them.
    /// </para>
    /// </remarks>
    internal static string Skill(SkillHarness harness, string? mountPath)
    {
        bool openCode = harness == SkillHarness.OpenCode;

        string where = (mountPath, openCode) switch
        {
            (null, true) =>
                """
                `<mount>` below is where the tree is mounted. Your session context names it; if
                nothing does and you read this skill from a tree, it is the path you read it from
                without `/skills/opencode/terminalfs/SKILL.md` on the end.
                """,
            (null, false) =>
                """
                `<mount>` below is where the tree is mounted. Your session context names it; if
                nothing does, ask rather than guess.
                """,
            (_, true) => "The tree is mounted at `<mount>`.",
            (_, false) =>
                """
                The tree is mounted at `<mount>`. If your session context names a terminalfs
                mount of its own, that one is yours: use it wherever `<mount>` appears below.
                """,
        };

        string text = (openCode ? OpenCodeSkillText : ClaudeCodeSkillText)
            .Replace("<where>", where, StringComparison.Ordinal);

        return mountPath is null ? text : text.Replace("<mount>", At(mountPath), StringComparison.Ordinal);
    }

    /// <summary>
    /// opencode's skill: the write and the reads in one <c>execute</c> script, because every turn
    /// re-bills the whole conversation and the script makes a command one turn instead of three.
    /// </summary>
    private const string OpenCodeSkillText =
        """
        ---
        name: terminalfs
        description: >-
          Run shell commands by writing to a mounted filesystem instead of a command tool. Use
          when you need a command to keep running while you do something else, when its output is
          too large to read in one piece, or when you want to watch it as it goes. Covers running
          a command, reading its output as it grows, waiting for it and killing it.
        ---

        # Running commands through terminalfs

        <where>

        Writing a command to `<mount>/ctl/<name>` runs it; what it did appears under
        `<mount>/cmd/<name>/` as ordinary files.

        ## Run a command — in one tool call

        Do the write and the reads inside a single `execute` script, with `tools.file_write` and
        `tools.file_read` — the terminalfs plugin puts opencode's own write and read there. Never
        split them across turns: every extra turn re-bills the whole conversation, so a command
        should cost one turn, not two or three. Several commands can share one script; await each
        write to the tree before the next, since writes to it at once are refused.

        ```js
        const M = "<mount>", n = "build"; // name: letters, digits, _ - . max 64
        await tools.file_write({ path: `${M}/ctl/${n}`, content: "dotnet build 2>&1 | tail -40" });
        const state = (await tools.file_read({ path: `${M}/cmd/${n}/wait`     })).content;
        const out   = (await tools.file_read({ path: `${M}/cmd/${n}/stdout`   })).content;
        const code  = (await tools.file_read({ path: `${M}/cmd/${n}/exitcode` })).content;
        return { state, code, out };
        ```

        Read `wait` **before** `stdout`. `wait` blocks until the command stops and returns
        `completed`, `error`, or — after about 25 seconds — `running`; `stdout` is a file that
        grows, so reading it first silently returns partial output. Add `stderr` only when you
        need it.

        Without `execute`, write the command with the `write` tool on its own: with the plugin, its
        result also carries what the command did — how it ended, its exit code and its output — so
        it is still one call. Without the plugin, follow it with reads of `wait`, then `stdout`.

        The plugin checks each command against your `shell` permission rules, as if you had run
        it with a shell tool, before the write lands, and opencode may ask the user first. A
        refused write names the rule; inside a script it throws only `Unable to write`, and the
        rule is given after the script's result. Don't reword the command to get round it. Write
        the path out in full, as above.

        ## One name, one command

        A name runs once. Sequence **inside** the command, not across names: `a && b | c` is one
        command. Use a fresh name for the next one. Nothing runs until the ctl file is closed, so
        write it in a single operation. `<mount>/cmd/<name>` does not exist until there is
        something to report.

        ## Keep the command short and the output small

        What you generate is the most expensive thing you do, and re-reading output is the second.

        - Prefer one `sed -i -e … -e …` or one `awk` over an inline `python3 <<EOF` heredoc: an
          inline script is billed as generated text every time you send it. If you genuinely need
          a script, write it to a file once and re-run it by path.
        - Bound the output in the command itself — `| tail -40`, `| head`, `grep -c`, `wc -l`.
        - For a large result, page `stdout` with the read tool's range instead of pulling it
          whole.
        - Pick one mechanism per task. If you are using terminalfs, do not also search the same
          files with native `grep`/`read` tools; every switch is another turn.

        ## Watching something long-running

        `ls <mount>/cmd/<name>` tells you whether it is still going — `pid` and `kill` are there
        while it runs, `exitcode` once it has stopped. Re-read `stdout` for more as it arrives,
        opening it fresh each time; a handle held open will not see later output.
        `echo x > <mount>/cmd/<name>/kill` ends it and keeps what it produced.

        ## Do not clean up

        Command directories are removed on their own about a minute after the last read, and a
        name you never write to frees itself. Do not spend a turn on `rm -r`.

        ## When a write fails

        A mount can only report an error number — `Operation not permitted`, `File exists` — so
        look under `<mount>/cmd/<name>/`:

        - `command`, `status`, `reason` and nothing else — refused by a rule before it ran.
          `reason` names the rule. There is no output because nothing ran. Reword it and use a
          **different** name; the refused one stays taken until removed.
        - `File exists` — the name has already run, refused or not, or someone else is writing it.

        There is no terminal and no standard input: anything that prompts gets end-of-file. Pass
        the flag that avoids the prompt (`--yes`, `--non-interactive`) or pipe the answer in
        inside the command. Editors, pagers and REPLs cannot run here.

        """;

    /// <summary>
    /// Claude Code's skill: the write and the reads in one Bash call, in a fixed shape.
    /// </summary>
    /// <remarks>
    /// The shape is fixed — <c>cat &gt; ctl/&lt;name&gt;</c> with a quoted <c>'CMD'</c> heredoc,
    /// then reads under <c>cmd/&lt;name&gt;/</c> — so that a permission hook can read the command
    /// out of the Bash call and check it. Loosen it here and a hook has to guess.
    /// </remarks>
    private const string ClaudeCodeSkillText =
        """
        ---
        name: terminalfs
        description: >-
          Run shell commands by writing them to a mounted filesystem with the Bash tool, one call
          per command. Use when you need a command to keep running while you do something else,
          when its output is too large to read in one piece, or when you want to watch it as it
          goes. Covers running a command, reading its output as it grows, waiting for it and
          killing it.
        ---

        # Running commands through terminalfs

        <where>

        Writing a command to `<mount>/ctl/<name>` runs it; what it did appears under
        `<mount>/cmd/<name>/` as ordinary files.

        ## Run a command — in one Bash call

        Write the command and read what it did in the same Bash call, in exactly this shape:

        ```sh
        cat > <mount>/ctl/build <<'CMD'
        dotnet build 2>&1 | tail -40
        CMD
        cat <mount>/cmd/build/wait; cat <mount>/cmd/build/stdout
        ```

        Never split it across calls: every extra call re-reads the whole conversation, so a
        command should cost one call, not two or three.

        Keep to the shape — `cat >` into `ctl/<name>`, the command in a heredoc quoted as
        `'CMD'`, then reads under `cmd/<name>/` — rather than `echo … >` or the Write tool. It is
        the shape a permission check can read the command out of: the terminalfs plugin checks it
        against your permission rules as if you had run it with Bash, and refuses a write into
        the tree in any other shape, or anything after it in the same call but reads of this
        tree. The command can be several lines, but none of them can be just `CMD`. Write the
        path out in full each time, not through a variable or after a `cd`: the check cannot read
        a command written to a path it cannot see, and refuses the ones it notices.

        A refusal says why. `denied by` names the rule and the settings file it is in; don't
        reword the command to get round it.

        Read `wait` **before** `stdout`. `wait` blocks until the command stops and prints
        `completed`, `error`, or — after about 25 seconds — `running`; `stdout` is a file that
        grows, so reading it first silently returns partial output. Put `2>&1` in the command
        rather than reading `stderr` separately, and add `; cat <mount>/cmd/build/exitcode` to the
        last line when the number matters.

        `running` means it is still going. Read again, still in one call:
        `cat <mount>/cmd/build/wait; cat <mount>/cmd/build/stdout`.

        ## One name, one command

        A name runs once. Sequence **inside** the command, not across names: `a && b | c` is one
        command. Give the next one a name you have not used yet in this session — letters,
        digits, `_`, `-` and `.`, up to 64 — not `build` again. Nothing runs until the ctl file is
        closed, and `<mount>/cmd/<name>` does not exist until there is something to report.

        ## Keep the command short and the output small

        What you generate is the most expensive thing you do, and re-reading output is the second.

        - Bound the output in the command itself — `| tail -40`, `| head`, `grep -c`, `wc -l`.
        - Prefer one `sed -i -e … -e …` or one `awk` over an inline `python3` script: an inline
          script is billed as generated text every time you send it. If you genuinely need a
          script, write it to a file once and re-run it by path.

        ## Watching something long-running

        `ls <mount>/cmd/<name>` tells you whether it is still going — `pid` and `kill` are there
        while it runs, `exitcode` once it has stopped. Read `stdout` again for more as it
        arrives; `tail -n 40` works on it. `echo x > <mount>/cmd/<name>/kill` ends it and keeps
        what it produced.

        ## Do not clean up

        Command directories are removed on their own about a minute after the last read, and a
        name you never write to frees itself. Do not spend a call on `rm -r`.

        ## When a write fails

        A mount can only report an error number — `Operation not permitted`, `File exists` — and
        the `cat >` fails with it. **When it does, what the reads after it print is not your
        command's.** Look under `<mount>/cmd/<name>/`:

        - `command`, `status`, `reason` and nothing else — refused by a rule before it ran.
          `cat <mount>/cmd/<name>/reason` names the rule. There is no output because nothing
          ran. Reword it and use a **different** name; the refused one stays taken until removed.
        - `File exists` — the name has already run, refused or not, or someone else is writing it.
          The reads print that earlier command's state and output. Run yours again under a name
          you have not used.

        There is no terminal and no standard input: anything that prompts gets end-of-file. Pass
        the flag that avoids the prompt (`--yes`, `--non-interactive`) or pipe the answer in
        inside the command. Editors, pagers and REPLs cannot run here.

        """;

    /// <summary>
    /// A mount path as it is written into the skill: one trailing separator taken off, so
    /// <c>&lt;mount&gt;/ctl/build</c> cannot become <c>//ctl/build</c>.
    /// </summary>
    /// <remarks>
    /// The separators themselves are left as they came. Translating them would be guessing which
    /// side of a namespace boundary the reader is on, which is the mistake this whole substitution
    /// exists to avoid.
    /// </remarks>
    private static string At(string mountPath) =>
        mountPath.TrimEnd('/', '\\') is { Length: > 0 } trimmed ? trimmed : mountPath;

    private static string Count(int commands) => commands switch
    {
        0 => "nothing",
        1 => "1 command",
        _ => commands.ToString(CultureInfo.InvariantCulture) + " commands",
    };

    private static string Rules(int count) => count == 1 ? "1 rule is" : $"{count} rules are";

    /// <summary>
    /// A command on one line, for a table cell. A multi-line command is shown by its first line
    /// with what follows marked, because a table row cannot carry a newline and silently showing
    /// only the first line would misrepresent what ran.
    /// </summary>
    private static string OneLine(string text)
    {
        string trimmed = text.Trim();

        int newline = trimmed.IndexOf('\n', StringComparison.Ordinal);

        if (newline < 0)
        {
            return trimmed.Replace("`", "'", StringComparison.Ordinal);
        }

        return trimmed[..newline].Trim().Replace("`", "'", StringComparison.Ordinal) + " …";
    }
}
