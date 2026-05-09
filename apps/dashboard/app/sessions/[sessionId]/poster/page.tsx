import PosterClient from "./poster-client";

export default async function PosterPage({
  params,
}: {
  params: Promise<{ sessionId: string }>;
}) {
  const { sessionId } = await params;
  return <PosterClient sessionId={sessionId} />;
}
