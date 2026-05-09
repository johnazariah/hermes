import { useState, useCallback } from "react";
import { useNavigate } from "react-router-dom";
import { useTheme } from "../hooks/useTheme";

const PLACEHOLDER_TEXT = `I have two investment properties:
- 1 Avalon St, Richmond
- 35 Manorwoods Dr, Wantirna

I work at Microsoft.
Anything from ATO is tax-related.`;

async function savePreferences(text: string): Promise<void> {
    await fetch("/api/preferences", {
        method: "PUT",
        headers: { "Content-Type": "text/plain" },
        body: text,
    });
}

function StepIndicator({ current }: { current: number }) {
    return (
        <div className="flex items-center justify-center gap-2 mb-8">
            {[1, 2, 3].map((step) => (
                <div
                    key={step}
                    className={`h-2 rounded-full transition-all duration-300 ${
                        step === current
                            ? "w-8 bg-blue-500"
                            : step < current
                              ? "w-2 bg-blue-400"
                              : "w-2 bg-neutral-300 dark:bg-neutral-700"
                    }`}
                />
            ))}
        </div>
    );
}

function StepWelcome({
    preferences,
    onPreferencesChange,
    onNext,
}: {
    preferences: string;
    onPreferencesChange: (v: string) => void;
    onNext: () => void;
}) {
    const [saving, setSaving] = useState(false);

    const handleNext = useCallback(async () => {
        setSaving(true);
        try {
            if (preferences.trim()) {
                await savePreferences(preferences.trim());
            }
            onNext();
        } finally {
            setSaving(false);
        }
    }, [preferences, onNext]);

    return (
        <div className="animate-fade-in">
            <div className="text-center mb-6">
                <span className="text-5xl mb-4 block">👋</span>
                <h1 className="text-2xl font-bold text-neutral-900 dark:text-white mb-2">
                    Welcome to Hermes
                </h1>
                <p className="text-neutral-500 dark:text-neutral-400">
                    Tell me about yourself — I'll learn the rest from your
                    documents.
                </p>
            </div>

            <textarea
                value={preferences}
                onChange={(e) => onPreferencesChange(e.target.value)}
                placeholder={PLACEHOLDER_TEXT}
                rows={7}
                className="w-full rounded-xl border border-neutral-200 dark:border-neutral-700 bg-neutral-50 dark:bg-neutral-800 text-neutral-900 dark:text-neutral-100 px-4 py-3 text-sm resize-none focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent placeholder:text-neutral-400 dark:placeholder:text-neutral-500 transition-colors"
            />

            <button
                onClick={handleNext}
                disabled={saving}
                className="mt-6 w-full py-3 rounded-xl bg-blue-600 hover:bg-blue-700 text-white font-medium text-sm transition-colors disabled:opacity-50 disabled:cursor-not-allowed cursor-pointer"
            >
                {saving ? "Saving…" : "Next →"}
            </button>
        </div>
    );
}

function StepConnect({ onNext }: { onNext: () => void }) {
    const navigate = useNavigate();

    return (
        <div className="animate-fade-in">
            <div className="text-center mb-6">
                <span className="text-5xl mb-4 block">📬</span>
                <h1 className="text-2xl font-bold text-neutral-900 dark:text-white mb-2">
                    Connect your email
                </h1>
                <p className="text-neutral-500 dark:text-neutral-400">
                    Hermes learns from your documents. Connect an email account
                    to get started, or skip and add files manually later.
                </p>
            </div>

            <div className="space-y-3">
                <button
                    onClick={() => {
                        localStorage.setItem("hermes-onboarded", "true");
                        navigate("/settings");
                    }}
                    className="w-full flex items-center gap-3 px-4 py-3 rounded-xl border border-neutral-200 dark:border-neutral-700 hover:border-blue-400 dark:hover:border-blue-500 bg-neutral-50 dark:bg-neutral-800 transition-colors cursor-pointer group"
                >
                    <span className="text-2xl">📧</span>
                    <div className="text-left flex-1">
                        <div className="text-sm font-medium text-neutral-900 dark:text-white">
                            Connect Gmail
                        </div>
                        <div className="text-xs text-neutral-500 dark:text-neutral-400">
                            Sync emails and attachments from Google
                        </div>
                    </div>
                    <span className="text-neutral-400 group-hover:text-blue-500 transition-colors">
                        →
                    </span>
                </button>

                <button
                    onClick={() => {
                        localStorage.setItem("hermes-onboarded", "true");
                        navigate("/settings");
                    }}
                    className="w-full flex items-center gap-3 px-4 py-3 rounded-xl border border-neutral-200 dark:border-neutral-700 hover:border-blue-400 dark:hover:border-blue-500 bg-neutral-50 dark:bg-neutral-800 transition-colors cursor-pointer group"
                >
                    <span className="text-2xl">📨</span>
                    <div className="text-left flex-1">
                        <div className="text-sm font-medium text-neutral-900 dark:text-white">
                            Connect Outlook
                        </div>
                        <div className="text-xs text-neutral-500 dark:text-neutral-400">
                            Sync emails and attachments from Microsoft
                        </div>
                    </div>
                    <span className="text-neutral-400 group-hover:text-blue-500 transition-colors">
                        →
                    </span>
                </button>
            </div>

            <button
                onClick={onNext}
                className="mt-6 w-full py-3 rounded-xl border border-neutral-200 dark:border-neutral-700 text-neutral-600 dark:text-neutral-400 hover:bg-neutral-100 dark:hover:bg-neutral-800 font-medium text-sm transition-colors cursor-pointer"
            >
                Skip for now →
            </button>
        </div>
    );
}

function StepReady({
    hasPreferences,
}: {
    hasPreferences: boolean;
}) {
    const navigate = useNavigate();

    const handleFinish = useCallback(() => {
        localStorage.setItem("hermes-onboarded", "true");
        navigate("/");
    }, [navigate]);

    return (
        <div className="animate-fade-in">
            <div className="text-center mb-6">
                <span className="text-5xl mb-4 block">🎉</span>
                <h1 className="text-2xl font-bold text-neutral-900 dark:text-white mb-2">
                    You're all set!
                </h1>
                <p className="text-neutral-500 dark:text-neutral-400">
                    Hermes is ready to start learning from your documents.
                </p>
            </div>

            <div className="rounded-xl border border-neutral-200 dark:border-neutral-700 bg-neutral-50 dark:bg-neutral-800 p-4 space-y-2 text-sm">
                <div className="flex items-center gap-2">
                    <span>{hasPreferences ? "✅" : "⏭️"}</span>
                    <span className="text-neutral-700 dark:text-neutral-300">
                        {hasPreferences
                            ? "Preferences saved"
                            : "Preferences skipped — you can add them in Settings"}
                    </span>
                </div>
                <div className="flex items-center gap-2">
                    <span>⏭️</span>
                    <span className="text-neutral-700 dark:text-neutral-300">
                        Email — connect anytime from Settings
                    </span>
                </div>
            </div>

            <button
                onClick={handleFinish}
                className="mt-6 w-full py-3 rounded-xl bg-blue-600 hover:bg-blue-700 text-white font-medium text-sm transition-colors cursor-pointer"
            >
                Go to Home →
            </button>
        </div>
    );
}

export function OnboardingPage() {
    const [step, setStep] = useState(1);
    const [preferences, setPreferences] = useState("");
    const { theme, toggleTheme } = useTheme();

    return (
        <div className="min-h-screen flex items-center justify-center bg-gradient-to-br from-neutral-100 to-neutral-200 dark:from-neutral-950 dark:to-neutral-900 px-4 transition-colors">
            {/* Theme toggle */}
            <button
                onClick={toggleTheme}
                className="fixed top-4 right-4 p-2 rounded-lg text-neutral-500 hover:bg-neutral-200 dark:hover:bg-neutral-800 transition-colors cursor-pointer"
                aria-label="Toggle theme"
            >
                {theme === "dark" ? "☀️" : "🌙"}
            </button>

            {/* Card */}
            <div className="w-full max-w-xl">
                <StepIndicator current={step} />
                <div className="bg-white dark:bg-neutral-900 rounded-2xl shadow-lg dark:shadow-neutral-950/50 border border-neutral-200 dark:border-neutral-800 p-8 transition-colors">
                    {step === 1 && (
                        <StepWelcome
                            preferences={preferences}
                            onPreferencesChange={setPreferences}
                            onNext={() => setStep(2)}
                        />
                    )}
                    {step === 2 && (
                        <StepConnect onNext={() => setStep(3)} />
                    )}
                    {step === 3 && (
                        <StepReady
                            hasPreferences={preferences.trim().length > 0}
                        />
                    )}
                </div>
            </div>
        </div>
    );
}
