import { BrowserRouter, Routes, Route } from "react-router-dom";
import { AppShell } from "./components/layout/AppShell";
import { HomePage } from "./pages/HomePage";
import { DocumentsPage } from "./pages/DocumentsPage";
import { SearchPage } from "./pages/SearchPage";
import { SettingsPage } from "./pages/SettingsPage";
import { OnboardingPage } from "./pages/OnboardingPage";

export default function App() {
    return (
        <BrowserRouter>
            <Routes>
                <Route path="/onboarding" element={<OnboardingPage />} />
                <Route element={<AppShell />}>
                    <Route index element={<HomePage />} />
                    <Route path="documents" element={<DocumentsPage />} />
                    <Route path="search" element={<SearchPage />} />
                    <Route path="settings" element={<SettingsPage />} />
                </Route>
            </Routes>
        </BrowserRouter>
    );
}
