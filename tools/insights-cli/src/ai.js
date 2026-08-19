import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { validateInsights } from "./schema.js";

const BaseUrl = "https://api.anthropic.com";
const MessagesPath = "/v1/messages";
const BetaHeader = "oauth-2025-04-20";

export async function generateInsightsFromHtml({ model, html }) {
  const credentials = await loadClaudeCredentials();
  if (!credentials?.accessToken) {
    throw new Error("Claude OAuth credentials not found. Run Claude Code to sign in.");
  }
  if (credentials.expiresAt && Date.now() > credentials.expiresAt) {
    throw new Error("Claude OAuth token expired. Please re-authenticate.");
  }

  const prompt = buildPrompt(html);
  const payload = {
    model: model || "claude-haiku-4-5",
    max_tokens: 1500,
    temperature: 0.2,
    system: "Return JSON only. Do not include markdown or commentary.",
    messages: [
      {
        role: "user",
        content: [
          {
            type: "text",
            text: prompt
          }
        ]
      }
    ]
  };

  const response = await fetch(`${BaseUrl}${MessagesPath}`, {
    method: "POST",
    headers: {
      Authorization: `Bearer ${credentials.accessToken}`,
      "content-type": "application/json",
      "anthropic-beta": BetaHeader,
      "anthropic-version": "2023-06-01"
    },
    body: JSON.stringify(payload)
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`Claude API error ${response.status}: ${errorText}`);
  }

  const json = await response.json();
  const text = extractText(json);
  const parsed = extractJson(text);
  return validateInsights(parsed);
}

function extractText(responseJson) {
  if (!responseJson?.content) {
    throw new Error("Claude API response missing content");
  }
  return responseJson.content
    .map((block) => (block.type === "text" ? block.text : ""))
    .join("\n")
    .trim();
}

function extractJson(text) {
  const first = text.indexOf("{");
  const last = text.lastIndexOf("}");
  if (first === -1 || last === -1) {
    throw new Error("Claude response did not include JSON");
  }
  const sliced = text.slice(first, last + 1);
  return JSON.parse(sliced);
}

function buildPrompt(html) {
  return `Extract structured data from the following Claude Code Insights HTML report.

Return JSON only that matches this schema exactly:
{
  "title": string,
  "dateRange": { "start": "YYYY-MM-DD", "end": "YYYY-MM-DD" },
  "totals": { "messages": number, "sessions": number, "days": number, "hours": number },
  "lines": { "added": number, "removed": number },
  "filesTouched": number,
  "messagesPerDay": number,
  "tools": [ { "name": string, "count": number } ],
  "languages": [ { "name": string, "lines": number, "percentage": number } ],
  "outcomes": [ { "name": string, "count": number } ],
  "satisfaction": [ { "name": string, "count": number } ],
  "multiFileChanges": number,
  "highlights": [ string, string ],
  "personalityBadge": { "label": string, "reason": string },
  "achievementRate": number,
  "timeOfDay": { "hourCounts": { "0": number, ... "23": number }, "peakPeriod": string },
  "roast": { "headline": string, "detail": string },
  "multiClauding": { "overlapEvents": number, "sessionsInvolved": number, "percentOfMessages": number },
  "peakPeriod": string,
  "medianResponseTime": number,
  "frictionCount": number,
  "frictionCategories": [ { "name": string, "count": number } ]
}

STRICT TEXT FORMATTING RULES:
- NEVER use EM dashes (—) or EN dashes (–). Use regular hyphens (-) only.
- NEVER use Oxford commas. Write "A, B and C" not "A, B, and C".
- Keep text concise and punchy. No flowery language.

DATA RULES:
- Use numeric values (no K/M suffixes).
- highlights should be short (<= 120 chars) and funny/roasty if possible.
- achievementRate should be 0-1 if derivable from outcomes. Calculate as (fully + mostly achieved) / total.
- roast: Extract from the "fun-ending" section. headline is the main quote. Make it funny and self-deprecating.
- multiClauding: Extract from "Multi-Clauding" section if present.
- peakPeriod: The time period with most activity. Must be one of: "Morning", "Afternoon", "Evening", "Night".
- medianResponseTime: The median response time in seconds from the report.
- frictionCount: Total number of friction events from the "Friction Categories" section. Sum all friction counts.
- frictionCategories: Extract from "Friction Categories" section. Common ones: Incomplete Error Discovery, Premature Implementation, Scope Drift, etc.
- If a field is missing set it to 0 or an empty array but keep the key.
- Do not return null for required numeric fields.

HTML:
${html}`;
}

async function loadClaudeCredentials() {
  const home = os.homedir();
  const credentialsPath = path.join(home, ".claude", ".credentials.json");
  try {
    const json = await fs.readFile(credentialsPath, "utf8");
    const data = JSON.parse(json);
    const oauth = data?.claudeAiOauth;
    if (!oauth) {
      return null;
    }
    return {
      accessToken: oauth.accessToken ?? null,
      refreshToken: oauth.refreshToken ?? null,
      expiresAt: typeof oauth.expiresAt === "number" ? oauth.expiresAt : null,
      subscriptionType: oauth.subscriptionType ?? null,
      rateLimitTier: oauth.rateLimitTier ?? null
    };
  } catch {
    return null;
  }
}
