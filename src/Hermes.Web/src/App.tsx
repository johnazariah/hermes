import { BrowserRouter, Routes, Route } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import { HomePage } from "./pages/HomePage";
import { DocumentsPage } from "./pages/DocumentsPage";
import { SearchPage } from "./pages/SearchPage";
import { SettingsPage } from "./pages/SettingsPage";
import { OnboardingPage } from "./pages/OnboardingPage";
import { PipelinePage } from "./pages/PipelinePage";
import { ChatPage } from "./pages/ChatPage";
import { useEffect } from "react";

export default function App() {
    useEffect(() => {
        const root = document.getElementById("root");
        if (!root) throw new Error("Canonical React root is missing");
        root.dataset.hermesApp = "canonical-react";
        return () => { delete root.dataset.hermesApp; };
    }, []);
    return (
        <BrowserRouter>
            <Routes>
                <Route path="/onboarding" element={<OnboardingPage />} />
                <Route element={<AppShell />}>
                    <Route index element={<HomePage />} />
                    <Route path="documents" element={<DocumentsPage />} />
                    <Route path="search" element={<SearchPage />} />
                    <Route path="pipeline" element={<PipelinePage />} />
                    <Route path="chat" element={<ChatPage />} />
                    <Route path="settings" element={<SettingsPage />} />
                </Route>
            </Routes>
        </BrowserRouter>
    );
}
