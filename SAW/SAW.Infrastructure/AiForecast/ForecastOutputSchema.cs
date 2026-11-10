using System.Text.Json;

namespace SAW.Infrastructure.AiForecast;

internal static class ForecastOutputSchema
{
    internal static readonly JsonElement Value = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "forecasts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "productBatchId": { "type": "integer" },
                  "predictedLossPercent": { "type": ["number", "null"] },
                  "safeStorageRemainingDays": { "type": ["integer", "null"] },
                  "riskLevel": { "type": "string", "enum": ["LOW", "MEDIUM", "HIGH", "CRITICAL"] },
                  "forecastHorizonDays": { "type": "integer" },
                  "recommendation": { "type": "string" },
                  "actions": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "priority": { "type": "string", "enum": ["IMMEDIATE", "URGENT", "RECOMMENDED", "MONITOR"] },
                        "category": { "type": "string", "enum": ["TEMPERATURE", "HUMIDITY", "ROTATION", "DISPOSAL", "INSPECTION", "TRANSFER", "OTHER"] },
                        "action": { "type": "string" },
                        "reason": { "type": "string" },
                        "deadline": { "type": ["string", "null"] }
                      },
                      "required": ["priority", "category", "action", "reason", "deadline"],
                      "additionalProperties": false
                    }
                  },
                  "analysisNotes": { "type": ["string", "null"] }
                },
                "required": [
                  "productBatchId", "predictedLossPercent", "safeStorageRemainingDays", "riskLevel",
                  "forecastHorizonDays", "recommendation", "actions", "analysisNotes"
                ],
                "additionalProperties": false
              }
            }
          },
          "required": ["forecasts"],
          "additionalProperties": false
        }
        """);
}
