import { Sidebar } from './Sidebar';

export function Shell({ selectedCategory, onSelectCategory, children }: {
  selectedCategory: string | null;
  onSelectCategory: (category: string | null) => void;
  children: React.ReactNode;
}) {
  return (
    <div className="flex h-screen bg-neutral-950 text-neutral-200">
      <Sidebar selectedCategory={selectedCategory} onSelectCategory={onSelectCategory} />
      <main className="flex-1 overflow-y-auto p-6">
        {children}
      </main>
    </div>
  );
}
