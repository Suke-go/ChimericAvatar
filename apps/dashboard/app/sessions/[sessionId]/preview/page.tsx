import PreviewClient from "./preview-client";

export default async function PreviewPage({
  params,
}: {
  params: Promise<{ sessionId: string }>;
}) {
  const { sessionId } = await params;
  return <PreviewClient sessionId={sessionId} />;
}
