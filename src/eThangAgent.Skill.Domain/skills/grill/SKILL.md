---
name: grill
description: Interview the user to extract real requirements before creative work begins. Use when a request is thin or underspecified and the real goal, constraints, and success criteria are unclear.
---

This is the observation leg of the scientific loop: the interview is where the task's
question is formed and the evidence — what the user already knows — is gathered before
any hypothesis is stated.

Interview the user relentlessly until you reach a shared understanding. Map their
request as a **design tree**: every decision branches into the decisions that hang
off it. This skill runs whenever the input is thin — a bare
question, a one-line request — because a plan built on an unexamined ask wastes
the whole downstream process.

## Speaking to the user

Every message addressed TO THE USER speaks to them as a user/stakeholder, not a
developer — plain language, user-visible consequences, no code vocabulary. Where a
decision genuinely requires technical framing, present it as a recommendation the
user approves or rejects rather than a technical question. This governs messages to
the user only: plans, task specs, and chain step briefs stay technical.

Work the tree in **rounds**. The **frontier** is every decision whose prerequisites
are already settled: the questions you can ask _now_ without guessing at answers
you haven't heard yet. Ask the whole frontier in one round: number each question
and give your recommended answer. Then wait for the user's answers before the next
round. Never ration questions one at a time — a round replaces a queue.

Format a round like so:

    Q1 - <question title>: <question body, might be multiple paragraphs, including
    multiple choices>

    -> <your recommended answer>

    ---

    Q2 - <question title>: <question body>

    -> <your recommended answer>

Each round the user answers reshapes the tree: settled decisions push the frontier
outward and unblock questions that depended on them. Recompute the frontier and ask
the next round. A question whose answer depends on another question still open in
this round belongs to a _later_ round, not this one.

Finding _facts_ is your job, never the user's. When a frontier question needs a fact
from the environment (filesystem, tools, etc.), look it up yourself; don't ask the
user for anything you could find out alone. Don't block on it: an open lookup is an
unsettled prerequisite, so only the questions downstream of it wait for the answer;
ask the rest of the frontier now.
The _decisions_ are the user's: put each to them and wait.

The session is done when the frontier is empty: every branch of the design tree
visited, nothing left silently assumed. Do not act on the request until the user
confirms you have reached a shared understanding — then hand off to writing-plans
for an unbounded request, or straight to work when it turned out to be bounded.
