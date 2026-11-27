import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Button,
  Card,
  Chip,
  Label,
  ListBox,
  SearchField,
  Select,
  Spinner,
  Surface,
  Switch,
} from '@heroui/react';
import { CopyPlus, Image as ImageIcon, RefreshCw, Zap } from 'lucide-react';
import { apiGet, apiPost, resolveApiUrl } from '../api';
import { CodeEditor } from '../components/CodeEditor';
import { useInfiniteScroll } from '../hooks/useInfiniteScroll';
import { fmt, type I18nDict } from '../i18n';
import type {
  ModEntry,
  StudioDraftFromVanilla,
  VanillaCardSummary,
  VanillaCatalogResponse,
  VanillaCategoryKey,
  VanillaDetailResponse,
} from '../types';

interface VanillaCatalogTabProps {
  t: I18nDict;
  mods: ModEntry[];
  activeModId: string;
  category: VanillaCategoryKey;
  inspectorOpen: boolean;
  onCountsChange: (counts: Record<VanillaCategoryKey, number>) => void;
  onSelectActiveMod: (modId: string) => void;
  onCloneToStudioDraft: (draft: StudioDraftFromVanilla) => void;
  onQuickCloned: (modId: string, category: VanillaCategoryKey, clonedKey: string) => void;
  onNotify: (msg: string, kind?: 'ok' | 'err' | 'info') => void;
}

const PAGE_SIZE = 60;

export function VanillaCatalogTab({
  t,
  mods,
  activeModId,
  category,
  inspectorOpen,
  onCountsChange,
  onSelectActiveMod,
  onCloneToStudioDraft,
  onQuickCloned,
  onNotify,
}: VanillaCatalogTabProps) {
  const [query, setQuery] = useState('');
  const [debouncedQuery, setDebouncedQuery] = useState('');
  const [items, setItems] = useState<VanillaCardSummary[]>([]);
  const [version, setVersion] = useState('');
  const [total, setTotal] = useState(0);
  const [hasMore, setHasMore] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const [detail, setDetail] = useState<VanillaDetailResponse | null>(null);
  const [loadingDetail, setLoadingDetail] = useState(false);
  const [keepVanillaId, setKeepVanillaId] = useState(false);
  const [cloning, setCloning] = useState(false);

  const scrollContainerRef = useRef<HTMLDivElement | null>(null);
  const reqIdRef = useRef(0);

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(query.trim()), 200);
    return () => clearTimeout(timer);
  }, [query]);

  const fetchPage = useCallback(
    async (cat: VanillaCategoryKey, q: string, offset: number, append: boolean, refresh = false) => {
      const myReq = ++reqIdRef.current;
      setLoading(true);
      setError(null);
      try {
        const qs = new URLSearchParams({
          category: cat,
          q,
          offset: String(offset),
          limit: String(PAGE_SIZE),
          ...(refresh ? { refresh: '1' } : {}),
        });
        const data = await apiGet<VanillaCatalogResponse>(`/api/vanilla/catalog?${qs.toString()}`);
        if (myReq !== reqIdRef.current) return;
        setVersion(data.version);
        onCountsChange(data.counts);
        setTotal(data.total);
        setHasMore(data.hasMore);
        setItems((prev) => (append ? [...prev, ...data.items] : data.items));
        if (!append && data.items.length > 0) {
          setSelectedKey(data.items[0].key);
        } else if (!append) {
          setSelectedKey(null);
          setDetail(null);
        }
      } catch (e) {
        if (myReq !== reqIdRef.current) return;
        setError((e as Error).message);
        if (!append) setItems([]);
      } finally {
        if (myReq === reqIdRef.current) setLoading(false);
      }
    },
    [onCountsChange],
  );

  useEffect(() => {
    scrollContainerRef.current?.scrollTo({ top: 0 });
    fetchPage(category, debouncedQuery, 0, false);
  }, [category, debouncedQuery, fetchPage]);

  useEffect(() => {
    if (!selectedKey) {
      setDetail(null);
      return;
    }
    let cancelled = false;
    setLoadingDetail(true);
    const qs = new URLSearchParams({ category, key: selectedKey });
    apiGet<VanillaDetailResponse>(`/api/vanilla/detail?${qs.toString()}`)
      .then((res) => {
        if (!cancelled) setDetail(res);
      })
      .catch((e) => {
        if (!cancelled) onNotify((e as Error).message, 'err');
      })
      .finally(() => {
        if (!cancelled) setLoadingDetail(false);
      });
    return () => {
      cancelled = true;
    };
  }, [category, selectedKey, onNotify]);

  const loadMore = useCallback(() => {
    if (loading || !hasMore) return;
    fetchPage(category, debouncedQuery, items.length, true);
  }, [loading, hasMore, fetchPage, category, debouncedQuery, items.length]);

  const sentinelRef = useInfiniteScroll({
    hasMore,
    loading,
    onLoadMore: loadMore,
    rootRef: scrollContainerRef,
  });

  const handleQuickClone = async () => {
    if (!detail) return;
    const targetModId = activeModId || mods[0]?.id;
    if (!targetModId) {
      onNotify(t.noModSelected, 'err');
      return;
    }
    setCloning(true);
    try {
      const res = await apiPost<{ category: VanillaCategoryKey; clonedKey: string }>('/api/vanilla/clone', {
        modId: targetModId,
        category: detail.category,
        key: detail.key,
        keepVanillaIdentity: keepVanillaId,
      });
      onNotify(fmt(t.toastCloned, { title: detail.title, modId: targetModId, key: res.clonedKey }), 'ok');
      onQuickCloned(targetModId, res.category, res.clonedKey);
    } catch (e) {
      onNotify((e as Error).message, 'err');
    } finally {
      setCloning(false);
    }
  };

  return (
    <div className="flex h-full min-h-0 flex-1 overflow-hidden">
      {/* Center: Slim Toolbar + Infinite Scroll Gallery */}
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="flex h-11 shrink-0 items-center gap-2 border-b border-border bg-background px-3">
          <SearchField
            aria-label="Search vanilla catalog"
            variant="secondary"
            value={query}
            onChange={setQuery}
            className="max-w-md flex-1"
          >
            <SearchField.Group className="h-7">
              <SearchField.SearchIcon />
              <SearchField.Input placeholder={t.searchVanilla} className="w-full text-xs" />
              <SearchField.ClearButton />
            </SearchField.Group>
          </SearchField>

          <div className="ml-auto flex shrink-0 items-center gap-2">
            <span className="font-mono text-[11px] text-muted">
              {items.length} / {total}
              {version ? ` · v${version}` : ''}
            </span>
            <Button
              size="sm"
              variant="secondary"
              onPress={() => fetchPage(category, debouncedQuery, 0, false, true)}
              className="h-7 text-xs"
            >
              <RefreshCw className={`size-3.5 ${loading ? 'animate-spin' : ''}`} />
              {t.refresh}
            </Button>
          </div>
        </div>

        <div ref={scrollContainerRef} className="min-h-0 flex-1 overflow-y-auto p-3">
          {error ? (
            <Alert status="danger">
              <Alert.Indicator />
              <Alert.Content>
                <Alert.Title>{t.vanillaUnavailableTitle}</Alert.Title>
                <Alert.Description>{error}</Alert.Description>
              </Alert.Content>
            </Alert>
          ) : (
            <>
              <div className="grid grid-cols-2 gap-2.5 sm:grid-cols-3 md:grid-cols-4 xl:grid-cols-5 2xl:grid-cols-6">
                {items.map((item) => {
                  const isSel = selectedKey === item.key;
                  return (
                    <button
                      key={`${category}-${item.key}`}
                      type="button"
                      onClick={() => setSelectedKey(item.key)}
                      className={`group flex flex-col overflow-hidden rounded-xl border text-left transition-all duration-150 ${
                        isSel
                          ? 'border-accent bg-accent-soft/40 ring-1 ring-accent/40'
                          : 'border-border/80 bg-surface hover:-translate-y-0.5 hover:border-foreground/25'
                      }`}
                    >
                      <div className="relative flex aspect-[3/4] w-full items-center justify-center overflow-hidden bg-background p-1.5">
                        {item.imageUrl ? (
                          <img
                            src={resolveApiUrl(item.imageUrl)}
                            alt={item.title}
                            loading="lazy"
                            decoding="async"
                            className="h-full w-full object-contain transition-transform duration-200 group-hover:scale-[1.03]"
                          />
                        ) : (
                          <ImageIcon className="size-7 text-muted/35" />
                        )}
                      </div>
                      <div className="flex flex-1 flex-col justify-between gap-1 p-2">
                        <div>
                          <div className="truncate text-xs font-semibold text-foreground">{item.title}</div>
                          <div className="truncate text-[10px] text-muted">{item.subtitle}</div>
                        </div>
                        <span className="w-fit max-w-full truncate rounded-md bg-surface-secondary px-1.5 py-0.5 font-mono text-[10px] text-accent">
                          {item.badge}
                        </span>
                      </div>
                    </button>
                  );
                })}
              </div>

              <div ref={sentinelRef} className="flex items-center justify-center py-5 text-xs text-muted">
                {loading ? (
                  <span className="inline-flex items-center gap-2 text-accent">
                    <Spinner size="sm" />
                    {t.loadingMore}
                  </span>
                ) : hasMore ? (
                  <span>{t.scrollForMore}</span>
                ) : items.length > 0 ? (
                  <span>
                    {t.allLoaded} ({items.length})
                  </span>
                ) : (
                  <span>{t.noMatches}</span>
                )}
              </div>
            </>
          )}
        </div>
      </div>

      {/* Right Collapsible Inspector Drawer */}
      {inspectorOpen && (
        <aside className="fixed inset-y-0 top-10 right-0 z-20 flex w-full max-w-[440px] shrink-0 flex-col overflow-y-auto border-l border-border bg-background p-3.5 shadow-2xl lg:static lg:z-auto lg:w-[400px] lg:max-w-none lg:bg-surface/40 lg:shadow-none xl:w-[440px]">
          {loadingDetail && !detail ? (
            <div className="flex flex-1 items-center justify-center">
              <Spinner size="md" />
            </div>
          ) : detail ? (
            <div className="space-y-3">
              <div className="flex gap-3">
                <div className="flex h-36 w-28 shrink-0 items-center justify-center overflow-hidden rounded-xl border border-border bg-background p-1">
                  {detail.imageUrl ? (
                    <img
                      src={resolveApiUrl(detail.imageUrl)}
                      alt={detail.title}
                      className="h-full w-full object-contain"
                    />
                  ) : (
                    <ImageIcon className="size-7 text-muted/40" />
                  )}
                </div>
                <div className="min-w-0 flex-1 space-y-1.5">
                  <h3 className="text-sm font-bold leading-snug text-foreground">{detail.title}</h3>
                  <p className="text-xs font-medium text-accent">{detail.subtitle}</p>
                  <Surface variant="secondary" className="rounded-lg border border-border/60 px-2 py-1 font-mono text-[10px] text-muted">
                    {detail.meta}
                  </Surface>
                </div>
              </div>

              <Card variant="default" className="p-3 gap-2.5">
                {mods.length > 0 && (
                  <Select
                    aria-label={t.targetModLabel}
                    fullWidth
                    variant="secondary"
                    value={activeModId || mods[0].id}
                    onChange={(k) => k && onSelectActiveMod(String(k))}
                  >
                    <Label className="text-[11px] text-muted">{t.targetModLabel}</Label>
                    <Select.Trigger className="h-8 text-xs">
                      <Select.Value />
                      <Select.Indicator />
                    </Select.Trigger>
                    <Select.Popover>
                      <ListBox>
                        {mods.map((m) => (
                          <ListBox.Item key={m.id} id={m.id} textValue={`${m.name} (${m.id})`}>
                            {m.name} ({m.id})
                            <ListBox.ItemIndicator />
                          </ListBox.Item>
                        ))}
                      </ListBox>
                    </Select.Popover>
                  </Select>
                )}

                <Switch size="sm" isSelected={keepVanillaId} onChange={setKeepVanillaId}>
                  <Switch.Content>
                    <Switch.Control>
                      <Switch.Thumb />
                    </Switch.Control>
                    <span className="text-xs text-foreground/90">{t.keepVanillaId}</span>
                  </Switch.Content>
                </Switch>

                <div className="grid grid-cols-1 gap-2 sm:grid-cols-2">
                  <Button
                    size="sm"
                    variant="primary"
                    fullWidth
                    onPress={() =>
                      onCloneToStudioDraft({
                        category: detail.category,
                        keepVanillaIdentity: keepVanillaId,
                        detail,
                      })
                    }
                    className="h-8 text-xs"
                  >
                    <CopyPlus className="size-3.5" />
                    {t.cloneToStudio}
                  </Button>
                  <Button
                    size="sm"
                    fullWidth
                    isPending={cloning}
                    isDisabled={mods.length === 0}
                    onPress={handleQuickClone}
                    className="button--success h-8 text-xs"
                  >
                    {({ isPending }) => (
                      <>
                        {isPending ? <Spinner color="current" size="sm" /> : <Zap className="size-3.5" />}
                        {t.quickClone}
                      </>
                    )}
                  </Button>
                </div>
              </Card>

              {detail.description && (
                <Surface
                  variant="secondary"
                  className="rounded-xl border border-border/60 p-3 text-xs leading-relaxed text-foreground/90 whitespace-pre-wrap"
                >
                  {detail.description}
                </Surface>
              )}

              {detail.subCards && detail.subCards.length > 0 && (
                <div className="space-y-1.5">
                  <div className="flex items-center justify-between text-xs font-semibold text-warning">
                    <span>{t.subCardsLabel}</span>
                    <Chip size="sm" variant="soft">{detail.subCards.length}</Chip>
                  </div>
                  <div className="grid grid-cols-4 gap-1.5 sm:grid-cols-5">
                    {detail.subCards.map((sc, idx) => (
                      <div
                        key={`${sc.id}-${idx}`}
                        className="flex flex-col items-center rounded-lg border border-border/70 bg-surface p-1 text-center"
                        title={`#${sc.id} ${sc.name}`}
                      >
                        <div className="flex aspect-[3/4] w-full items-center justify-center overflow-hidden rounded bg-background">
                          {sc.imageUrl ? (
                            <img
                              src={resolveApiUrl(sc.imageUrl)}
                              alt={sc.name}
                              loading="lazy"
                              className="h-full w-full object-contain"
                            />
                          ) : (
                            <ImageIcon className="size-4 text-muted/40" />
                          )}
                        </div>
                        <span className="mt-1 w-full truncate text-[9px] text-foreground/85">{sc.name}</span>
                      </div>
                    ))}
                  </div>
                </div>
              )}

              {detail.luaScript && detail.luaScript.trim().length > 0 && (
                <CodeEditor
                  value={detail.luaScript}
                  mode="lua"
                  readOnly
                  height="260px"
                  title={`${t.luaPreviewLabel} (c${detail.key}.lua)`}
                  copyLabel={t.copy}
                  copiedLabel={t.copied}
                />
              )}
            </div>
          ) : (
            <div className="flex flex-1 items-center justify-center text-xs text-muted text-center px-6">
              {t.vanillaEmptyHint}
            </div>
          )}
        </aside>
      )}
    </div>
  );
}
