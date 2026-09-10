/**
 * Vix de Saint-Quentin
 *
 * Indice composite experimental de stress economique, financier et social
 * pour la France et la zone Saint-Quentin / Aisne.
 *
 * IMPORTANT : cet indice n'est ni le VIX officiel du Cboe, ni un produit
 * financier, ni une prevision certaine. C'est un indicateur analytique maison
 * sur une echelle de 0 a 100.
 */

export const VIX_SAINT_QUENTIN = Object.freeze({
  name: "Vix de Saint-Quentin",
  variable: "VIX_SAINT_QUENTIN",
  version: "1.0.0",

  value: 51.4,
  max: 100,
  status: "ORANGE",
  regime: "FRAGILITE_ELEVEE",

  // Date des dernieres donnees consolidees utilisees pour ce snapshot.
  asOf: "2026-09-09",
  // Derniere verification automatique du fichier.
  lastCheckedAt: "2026-09-10T01:39:00+02:00",

  subindices: Object.freeze({
    france: 47.8,
    saintQuentinAisne: 56.7
  }),

  scale: Object.freeze([
    Object.freeze({ min: 0, max: 24.99, level: "VERT", label: "Situation relativement stable" }),
    Object.freeze({ min: 25, max: 44.99, level: "JAUNE", label: "Tensions moderees" }),
    Object.freeze({ min: 45, max: 59.99, level: "ORANGE", label: "Fragilite elevee" }),
    Object.freeze({ min: 60, max: 74.99, level: "ROUGE", label: "Stress severe" }),
    Object.freeze({ min: 75, max: 100, level: "CRITIQUE", label: "Stress systemique" })
  ]),

  methodology: Object.freeze({
    formula: "weighted_average",
    weightsSum: 100,
    note: "Chaque composante est transformee en score de stress de 0 a 100, puis ponderee. Une hausse rapide de l'indice est plus informative qu'un niveau eleve mais stable.",

    components: Object.freeze([
      Object.freeze({ key: "oatBundSpread", label: "Spread OAT-Bund", weight: 12, rawValue: 85.5, unit: "pb", stressScore: 46.3, scope: "FRANCE" }),
      Object.freeze({ key: "oat10y", label: "OAT France 10 ans", weight: 8, rawValue: 4.25, unit: "%", stressScore: 68.8, scope: "FRANCE" }),
      Object.freeze({ key: "unemploymentFrance", label: "Chomage France", weight: 10, rawValue: 8.3, unit: "%", stressScore: 38.3, scope: "FRANCE" }),
      Object.freeze({ key: "businessFailures", label: "Defaillances d'entreprises", weight: 10, rawValue: 19, unit: "% vs moyenne 2010-2019", stressScore: 38.0, scope: "FRANCE" }),
      Object.freeze({ key: "consumerConfidence", label: "Confiance des menages", weight: 8, rawValue: 86, unit: "indice", stressScore: 63.3, scope: "FRANCE" }),
      Object.freeze({ key: "inflationEnergy", label: "Inflation et choc energetique", weight: 7, rawValue: 2.4, unit: "% inflation", stressScore: 39.1, scope: "FRANCE" }),
      Object.freeze({ key: "publicDeficit", label: "Deficit public", weight: 5, rawValue: 5.2, unit: "% PIB", stressScore: 44.0, scope: "FRANCE" }),
      Object.freeze({ key: "povertySaintQuentin", label: "Pauvrete Saint-Quentin", weight: 10, rawValue: 30.0, unit: "%", stressScore: 80.0, scope: "LOCAL" }),
      Object.freeze({ key: "unemploymentSaintQuentin", label: "Chomage Saint-Quentin (recensement)", weight: 8, rawValue: 22.6, unit: "%", stressScore: 69.2, scope: "LOCAL" }),
      Object.freeze({ key: "overindebtednessAisne", label: "Surendettement Aisne", weight: 8, rawValue: 472, unit: "depots / 100 000 habitants", stressScore: 71.6, scope: "LOCAL" }),
      Object.freeze({ key: "housingVacancy", label: "Vacance des logements Saint-Quentin", weight: 5, rawValue: 13.3, unit: "%", stressScore: 55.3, scope: "LOCAL" }),
      Object.freeze({ key: "localHiring", label: "Dynamique de recrutement locale", weight: 9, rawValue: 17.1, unit: "% variation annuelle", stressScore: 7.2, scope: "LOCAL", inverseRisk: true })
    ])
  })
});

/**
 * Recalcule l'indice a partir d'un tableau de composantes de la forme
 * [{ weight: 12, stressScore: 46.3 }, ...].
 */
export function calculateVixSaintQuentin(components) {
  if (!Array.isArray(components) || components.length === 0) {
    throw new TypeError("components doit etre un tableau non vide");
  }

  const totals = components.reduce(
    (acc, component) => {
      const weight = Number(component.weight);
      const score = Number(component.stressScore);

      if (!Number.isFinite(weight) || !Number.isFinite(score)) {
        throw new TypeError("Chaque composante doit avoir un weight et un stressScore numeriques");
      }

      return {
        weighted: acc.weighted + weight * score,
        weights: acc.weights + weight
      };
    },
    { weighted: 0, weights: 0 }
  );

  if (totals.weights <= 0) {
    throw new RangeError("La somme des poids doit etre strictement positive");
  }

  return Math.round((totals.weighted / totals.weights) * 10) / 10;
}

export function getVixSaintQuentinStatus(value) {
  const score = Math.max(0, Math.min(100, Number(value)));

  if (score < 25) return "VERT";
  if (score < 45) return "JAUNE";
  if (score < 60) return "ORANGE";
  if (score < 75) return "ROUGE";
  return "CRITIQUE";
}

export default VIX_SAINT_QUENTIN;
