import { BrowserRouter, Routes, Route } from "react-router-dom";
import { AppLayout } from "./pages/AppLayout";
import { PipelinePage } from "./pages/PipelinePage";
import { DocumentsPage } from "./pages/DocumentsPage";
import { SearchPage } from "./pages/SearchPage";
import { ChatPage } from "./pages/ChatPage";
import { SettingsPage } from "./pages/SettingsPage";

export default function App() {
    return (
        <BrowserRouter>
            <Routes>
                <Route element={<AppLayout />}>
                    <Route index element={<PipelinePage />} />
                    <Route path="documents" element={<DocumentsPage />} />
                    <Route path="search" element={<SearchPage />} />
                    <Route path="chat" element={<ChatPage />} />
                    <Route path="settings" element={<SettingsPage />} />
                </Route>
            </Routes>
        </BrowserRouter>
    );
}
