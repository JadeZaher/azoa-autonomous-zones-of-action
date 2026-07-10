import express from "express";
import cors, { CorsOptions } from "cors";
import { config } from "./config";
import checkoutRoute from "./routes/checkout";
import webhookRoute from "./routes/webhook";

const app = express();

// CRITICAL-2: CORS allowlist instead of allow-any-origin. Non-browser callers
// (no Origin header) and same-origin requests are permitted; cross-origin
// browser requests must match the configured allowlist.
const corsOptions: CorsOptions = {
  origin(origin, callback) {
    if (!origin || config.allowedOrigins.includes(origin)) {
      return callback(null, true);
    }
    return callback(new Error("Origin not allowed by CORS policy"));
  },
};
app.use(cors(corsOptions));

// Webhooks require the raw body for Stripe signature verification.
app.use("/api/webhook", express.raw({ type: "application/json" }), webhookRoute);

// All other routes use JSON parsing.
app.use(express.json());
app.use("/api/checkout", checkoutRoute);

app.get("/health", (_req, res) => {
  res.json({ status: "healthy" });
});

app.listen(config.port, () => {
  console.log(`Fiat sidecar listening on port ${config.port}`);
});
