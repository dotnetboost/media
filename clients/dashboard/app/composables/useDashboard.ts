import { createSharedComposable } from '@vueuse/core'

const _useDashboard = () => {
  const router = useRouter()

  defineShortcuts({
    'g-h': () => router.push('/'),
    'g-m': () => router.push('/media'),
    'g-p': () => router.push('/products')
  })
}

export const useDashboard = createSharedComposable(_useDashboard)
