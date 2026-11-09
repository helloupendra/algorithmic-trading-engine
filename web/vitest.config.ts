import { configDefaults, defineConfig, mergeConfig } from 'vitest/config'
import vite from './vite.config'
export default mergeConfig(vite, defineConfig({ test: { exclude: [...configDefaults.exclude, 'e2e/**'] } }))
