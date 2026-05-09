import AvatarClient from "./avatar-client";

export default async function AvatarPage({
  params,
}: {
  params: Promise<{ sessionId: string }>;
}) {
  const { sessionId } = await params;
  return <AvatarClient sessionId={sessionId} />;
}
