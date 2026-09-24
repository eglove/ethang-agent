---
name: using-skills
description: Session contract for skill use: prefer a matching skill over improvising, announce the skill, load its body with skill_view, and follow it; user instructions always outrank skills
---

# Using Skills

Skills listed in the [skills listing] block are available. A skill that matches the
current task is better than improvising the same procedure from scratch.

1. Before starting non-trivial work, check the listing for a skill that matches the
   task. Match on what the task IS, not only on exact wording.
2. Load the skill with skill_view before relying on it, even if you believe you know
   its content: bodies evolve and details matter.
3. Announce: "Using [skill] to [purpose]" — then follow the skill.
4. If two skills match, run the process skill first (it sets the approach), then the
   implementation skill.
5. The user's explicit instructions outrank any skill. Skills the user names are
   loaded even when they are not auto-listed.
6. If no skill matches, proceed without one. Do not force a match.

This harness is eThang Agent. Tool binding lives in the ethang-tools-mapping skill.

Skills marked [manual] exist but are not auto-listed; the user loads them by name.
