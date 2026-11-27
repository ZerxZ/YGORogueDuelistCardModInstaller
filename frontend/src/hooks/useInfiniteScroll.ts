import { useEffect, useRef } from 'react';

/**
 * True IntersectionObserver infinite-scroll hook.
 * Observes a sentinel element at the bottom of a scrollable container and triggers
 * `onLoadMore` automatically when within `rootMargin` of the viewport edge.
 */
export function useInfiniteScroll(options: {
  hasMore: boolean;
  loading: boolean;
  onLoadMore: () => void;
  rootRef?: React.RefObject<HTMLElement | null>;
  rootMargin?: string;
}) {
  const { hasMore, loading, onLoadMore, rootRef, rootMargin = '320px' } = options;
  const sentinelRef = useRef<HTMLDivElement | null>(null);
  const callbackRef = useRef(onLoadMore);
  callbackRef.current = onLoadMore;

  useEffect(() => {
    const target = sentinelRef.current;
    if (!target || !hasMore || loading) return;

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            callbackRef.current();
            break;
          }
        }
      },
      {
        root: rootRef?.current ?? null,
        rootMargin,
        threshold: 0.01,
      },
    );

    observer.observe(target);
    return () => observer.disconnect();
  }, [hasMore, loading, rootRef, rootMargin]);

  return sentinelRef;
}
