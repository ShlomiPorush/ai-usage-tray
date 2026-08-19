import { z } from "zod";

const numberish = z.preprocess((value) => {
  if (typeof value === "string") {
    const cleaned = value.replace(/[^0-9.-]/g, "");
    if (cleaned.length > 0) {
      const parsed = Number(cleaned);
      return Number.isFinite(parsed) ? parsed : value;
    }
  }
  return value;
}, z.number());

const countItem = z.object({
  name: z.string(),
  count: numberish
});

const languageItem = z.object({
  name: z.string(),
  lines: numberish.optional(),
  percentage: numberish.optional()
});

const badgeSchema = z.object({
  label: z.string(),
  reason: z.string().optional()
});

export const insightsSchema = z.object({
  title: z.string().default("Claude Code Insights"),
  dateRange: z.object({
    start: z.string(),
    end: z.string()
  }),
  totals: z.object({
    messages: numberish,
    sessions: numberish,
    days: numberish,
    hours: numberish.optional()
  }),
  lines: z.object({
    added: numberish,
    removed: numberish
  }),
  filesTouched: numberish,
  messagesPerDay: numberish.optional(),
  tools: z.array(countItem),
  languages: z.array(languageItem).optional(),
  outcomes: z.array(countItem).optional(),
  satisfaction: z.array(countItem).optional(),
  multiFileChanges: z.number().optional(),
  highlights: z.array(z.string()).optional(),
  personalityBadge: badgeSchema.optional(),
  achievementRate: numberish.optional(),
  timeOfDay: z
    .object({
      hourCounts: z.record(numberish),
      peakPeriod: z.string().optional()
    })
    .optional(),
  roast: z
    .object({
      headline: z.string(),
      detail: z.string().optional()
    })
    .optional(),
  multiClauding: z
    .object({
      overlapEvents: numberish.optional(),
      sessionsInvolved: numberish.optional(),
      percentOfMessages: numberish.optional()
    })
    .optional(),
  peakPeriod: z.string().optional(),
  medianResponseTime: numberish.optional(),
  frictionCount: numberish.optional(),
  frictionCategories: z.array(countItem).optional()
});

export const enrichmentSchema = z.object({
  highlights: z.array(z.string()).min(1).max(2),
  personalityBadge: badgeSchema
});

export function validateInsights(data) {
  return insightsSchema.parse(data);
}

export function validateEnrichment(data) {
  return enrichmentSchema.parse(data);
}
