# Agent Rules
<RULE[user_workspace]>
- **No Ampersands Anywhere**: Do NOT use ampersands (`&` or `&amp;`) in ANY RimWorld files (including `About.xml` and `steam_description.txt`), as they can cause XML and string parsing errors in RimWorld's engine. Always spell out the word "and".
- **Terminal Execution**: Do not use PowerShell to save or persist variables. Instead, write a small `.bat` file, run it from the project directory, and clean it up when appropriate.
- **Tool Verification**: Always check MCP server/tool status and evaluate the available list of tools (using `list_permissions` or by checking tool declarations) before starting design or coding tasks in a session.
</RULE[user_workspace]>
