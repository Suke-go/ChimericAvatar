import KnowledgeClient from "./knowledge-client";

export default async function KnowledgePage({ params }: { params: Promise<{ sessionId: string }> }) {
  const { sessionId } = await params;
  return <KnowledgeClient sessionId={sessionId} />;
}
