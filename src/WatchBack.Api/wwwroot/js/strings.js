/**
 * Frontend strings localization utility
 * Fetches localized strings from the API and provides formatting helpers
 */

let cachedStrings = null;
let stringsPromise = null;

/**
 * Fetch localized strings from the API
 * Caches the result to avoid repeated requests
 */
export async function loadStrings() {
  if (cachedStrings) {
    return cachedStrings;
  }

  if (stringsPromise) {
    return stringsPromise;
  }

  stringsPromise = fetch('/api/strings')
    .then(response => {
      if (!response.ok) {
        throw new Error(`Failed to load strings: ${response.statusText}`);
      }
      return response.json();
    })
    .then(data => {
      cachedStrings = data;
      stringsPromise = null;
      return cachedStrings;
    })
    .catch(error => {
      console.error('Error loading strings:', error);
      stringsPromise = null;
      cachedStrings = {};
      return cachedStrings;
    });

  return stringsPromise;
}

/**
 * Get a string value by key
 * @param {string} key - The string key (e.g., "ConfigEndpoints_GetConfig_Server_URL")
 * @param {...*} values - Values to interpolate into {0}, {1}, etc. placeholders
 * @returns {string} The localized string with placeholders replaced
 */
export function getString(key, ...values) {
  if (!cachedStrings) {
    console.warn(`Strings not loaded yet. Requested key: ${key}`);
    return key;
  }

  let str = cachedStrings[key];
  if (!str) {
    console.warn(`String not found: ${key}`);
    return key;
  }

  // Replace {0}, {1}, etc. with provided values
  return str.replace(/{(\d+)}/g, (match, index) => {
    const idx = parseInt(index, 10);
    return values[idx] !== undefined ? values[idx] : match;
  });
}

/**
 * Get a string without interpolation
 * @param {string} key - The string key
 * @returns {string} The localized string
 */
export function getStringRaw(key) {
  if (!cachedStrings) {
    console.warn(`Strings not loaded yet. Requested key: ${key}`);
    return key;
  }

  return cachedStrings[key] || key;
}

/**
 * Format a string with values (alternative to using spread operator)
 * Useful when values come from an array
 * @param {string} key - The string key
 * @param {Array} values - Array of values to interpolate
 * @returns {string} The formatted string
 */
export function getStringFormatted(key, values = []) {
  return getString(key, ...values);
}
