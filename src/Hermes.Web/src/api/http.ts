export async function request(path: string, init?: RequestInit): Promise<Response> {
  const response = await fetch(path, init);
  if (!response.ok) throw new Error(`Service request failed (HTTP ${response.status})`);
  return response;
}
