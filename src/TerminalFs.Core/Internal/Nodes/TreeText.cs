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
            /ctl/<name>             write a command here to run it
            /cmd/<name>/command      the command, as it was written
            /cmd/<name>/pid          the process, while there is one
            /cmd/<name>/status       running, completed, error or denied
            /cmd/<name>/reason       why it was refused, when it was
            /cmd/<name>/exitcode     once it has stopped
            /cmd/<name>/stdout       what it wrote, as it writes it
            /cmd/<name>/stderr       the same for standard error
            /cmd/<name>/wait         reading this blocks until it stops
            /cmd/<name>/kill         write anything here to end it
            /skills/                how an agent should use this tree
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

            Nothing runs until the file is closed, so a command is never half-executed. A name runs
            **once**: writing to one that is already a command is refused until its directory is
            removed.

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
            long session does not fill up with old output.

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
                Nothing has been run yet. Write a command to `/ctl` and its directory appears
                here:

                ```text
                echo 'run first echo hello' > /ctl
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

          A name runs **once**, including one that was itself refused. Writing to one that is
          already a command is refused until its
          directory is removed.

          Each command has a file of its own rather than sharing one control file, because a
          client merges concurrent writes to a single path: four callers writing at once reached
          this server as one write, and three commands were lost without an error anywhere.

          If a write fails, look under `/cmd/<name>/` — the error a mount reports is only a
          number. A command refused before it ran is there with `status` reading `denied` and
          `reason` saying why. `File exists` means the name is already a command, and its
          directory is why.

          """;

    /// <summary>The skills directory: an index and the skill itself.</summary>
    internal static string SkillsIndex(DateTimeOffset builtAt) =>
        new Frontmatter(OkfType.Of(TerminalNodeKind.Skill))
            .Add("title", "Skills")
            .Add("description", "How an agent should use this tree.")
            .AddTimestamp(builtAt)
            .ToString()
        + """
          # Skills

          - [terminalfs](terminalfs/index.md) — running commands through this filesystem.

          Copy the skill into your own skills directory; nothing here runs from the mount.

          """;

    /// <summary>The page describing the skill, as opposed to the skill itself.</summary>
    internal static string SkillIndex(DateTimeOffset builtAt) =>
        new Frontmatter(OkfType.Of(TerminalNodeKind.Skill))
            .Add("title", "terminalfs")
            .Add("description", "Running shell commands by writing to a file.")
            .AddTimestamp(builtAt)
            .ToString()
        + """
          # terminalfs

          [SKILL.md](SKILL.md) is the skill. Copy it into your project's skills directory —
          `.claude/skills/terminalfs/SKILL.md` or wherever your harness looks — and replace
          `<mount>` with where this tree is mounted. Nothing here runs from the mount.

          """;

    /// <summary>
    /// The skill an agent harness reads. No OKF frontmatter: a skill file's frontmatter belongs
    /// to the harness, which matches on <c>name</c> and <c>description</c>, and inventing extra
    /// fields there would be noise.
    /// </summary>
    internal static string Skill =>
        """
        ---
        name: terminalfs
        description: >-
          Run shell commands by writing to a mounted filesystem instead of a command tool. Use
          when you need a command to keep running while you do something else, when its output is
          too large to read in one piece, or when you want to watch it as it goes. Covers running
          a command, reading its output as it grows, waiting for it, killing it and clearing up.
        ---

        # Running commands through terminalfs

        The tree is mounted at `<mount>`. Writing a command to `<mount>/ctl/<name>` runs it; what
        it did appears under `<mount>/cmd/<name>/` as ordinary files.

        ## Run something

        Pick the name yourself — it is the file you write to and the directory the results appear
        in. Letters, digits, `_`, `-` and `.`, up to 64 characters.

        ```sh
        echo 'dotnet build' > <mount>/ctl/build
        ```

        Nothing runs until the file is closed, so `echo … >` is one whole command. For several
        lines, use a heredoc:

        ```sh
        cat > <mount>/ctl/tests <<'EOF'
        dotnet test --no-build 2>&1 | tail -40
        EOF
        ```

        Sequencing belongs **inside** one command — `a && b | c` is one command, and a name runs
        once. To run something else, use another name.

        Each command is its own file, so you can start several at the same time without them
        interfering.

        **If the write fails, look under `<mount>/cmd/<name>/`.** A mount reports only an error
        number — `Operation not permitted`, `File exists` — because that is all 9P2000.L carries.
        A command refused before it ran still gets its directory:

        ```sh
        cat <mount>/cmd/build/status    # denied
        cat <mount>/cmd/build/reason    # denied by rule 'Bash(sudo:*)' in ~/.config/terminalfs/settings.json
        ```

        `File exists` is the other one: the name is already a command, refused or not.
        `ls <mount>/cmd/<name>` shows which, and `rm -r` frees the name — or use a different
        name, which is usually quicker.

        ## Find out what happened

        ```sh
        cat <mount>/cmd/build/wait        # blocks until it stops, then prints its state
        cat <mount>/cmd/build/exitcode    # 0, or what it failed with
        cat <mount>/cmd/build/stdout
        cat <mount>/cmd/build/stderr
        ```

        `wait` gives up after about twenty-five seconds and prints `running`. That means the
        command is still going, not that anything is wrong — read it again.

        `stdout` and `stderr` are real files that grow. Read them again for more, and use
        `tail -n 40`, `grep` and `wc -l` on them rather than reading a large one whole. Open them
        fresh each time: a handle held open will not see what arrives later.

        `ls <mount>/cmd/<name>` answers whether it is still going without reading anything —
        `pid` and `kill` are there while it runs, `exitcode` once it has stopped, and just
        `command`, `status` and `reason` if it was refused before it ran.

        ## Stop and clear up

        ```sh
        echo x > <mount>/cmd/build/kill   # end it, keeping what it produced
        rm -r <mount>/cmd/build           # remove it, ending it first if it is still running
        ```

        Removing a directory is worth doing when you are finished with a command; one that is
        left is removed on its own a minute after the last read. A refused command is cleared up
        the same way, and removing it is what frees its name.

        ## What this cannot do

        There is no terminal and no standard input: a command's stdin is closed at once, so
        anything that prompts gets end-of-file rather than waiting. Pass the flag that avoids the
        prompt (`--yes`, `--non-interactive`), or pipe the answer in inside the command itself.
        Full-screen programs — an editor, a pager, a REPL — cannot run here.

        Some commands are refused before they run, by rules in a settings file outside this tree.
        A refused command is a directory holding `command`, `status` and `reason` and nothing
        else — there is no output, because nothing ran. Reword it and write to a **different**
        name: the refused one stays taken until you remove it.

        """;

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
